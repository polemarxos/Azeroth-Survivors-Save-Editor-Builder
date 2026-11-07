Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>Writes the ticked findings (new entries, changed values, gear, a changed seal key) into the editor page.</summary>
    Public Module EditorBuilder

        ''' <summary>Loads each data table once, lets the changes edit it, and writes back only the tables that changed.</summary>
        Private Class Tables
            Private ReadOnly T As EditorTemplate
            Private ReadOnly Cache As New Dictionary(Of String, JsonNode)
            Private ReadOnly Dirty As New HashSet(Of String)
            Public Sub New(t As EditorTemplate)
                Me.T = t
            End Sub
            Public Function [Get](name As String) As JsonNode
                Dim n As JsonNode = Nothing
                If Not Cache.TryGetValue(name, n) Then n = T.GetJson(name) : Cache(name) = n
                Return n
            End Function
            Public Function Obj(name As String) As JsonObject
                Return TryCast([Get](name), JsonObject)
            End Function
            ''' <summary>A sub-object such as IC.pet or CODEX.J (created if missing).</summary>
            Public Function Sub_(name As String, key As String) As JsonObject
                Dim o = Obj(name)
                If o Is Nothing Then Return Nothing
                Dim s = TryCast(o(key), JsonObject)
                If s Is Nothing Then s = New JsonObject() : o(key) = s
                Return s
            End Function
            Public Sub Touch(name As String)
                Dirty.Add(name)
            End Sub
            Public Sub Put(name As String, node As JsonNode)
                Cache(name) = node : Dirty.Add(name)
            End Sub
            Public Sub Flush()
                For Each n In Dirty
                    If Cache(n) IsNot Nothing Then T.SetJson(n, Cache(n))
                Next
            End Sub
        End Class

        Public Function Apply(t As EditorTemplate, findings As IEnumerable(Of Finding), key As KeyResult) As List(Of String)
            Dim log As New List(Of String)
            Dim known = KnownData.FromTemplate(t)
            Dim tb As New Tables(t)
            ' rows whose id is still unknown ("?...") can't be written
            Dim pick = findings.Where(Function(f) f.Include AndAlso (f.Status = FindStatus.New OrElse f.Status = FindStatus.Changed) AndAlso
                                                  Cat.Writable.Contains(f.Category) AndAlso Not f.Id.StartsWith("?")).ToList()
            EnsureCodePatches(t)

            AddAchievements(tb, pick, log)
            AddZones(tb, pick, log)
            AddSimple(tb, pick, log)
            Talents(tb, known, pick, log)
            Legendary(tb, findings.Where(Function(f) f.Category = Cat.Legendary).ToList(), log)
            ChangedValues(tb, pick, log)
            Gear(t, tb, pick, log)
            tb.Flush()

            If key IsNot Nothing AndAlso key.Changed AndAlso key.Seal IsNot Nothing AndAlso
               findings.Any(Function(f) f.Category = Cat.Key AndAlso f.Include) Then
                t.SetSeal(key.Seal)
                log.Add("Seal key updated to " & key.Seal.Prefix & key.Seal.SecretHex & ".")
            End If

            t.StampVersion()
            log.Add("Version label set to: " & t.VersionLabel())
            Return log
        End Function

        Private Function J(s As String) As JsonNode
            Return JsonValue.Create(s)
        End Function

        Private Function NonEmpty(s As String, fallback As String) As String
            Return If(String.IsNullOrWhiteSpace(s), fallback, s.Trim())
        End Function

        Private Function ToInt(s As String, d As Integer) As Integer
            Dim v As Integer
            Return If(Integer.TryParse(s, v), v, d)
        End Function

        Private Function IconOf(f As Finding) As String
            Return NonEmpty(f.Icon, "INV_Misc_QuestionMark")
        End Function

        ' ---------- new entries ----------

        Private Sub AddAchievements(tb As Tables, pick As List(Of Finding), log As List(Of String))
            Dim achs = TryCast(tb.Get("ACHS"), JsonArray)
            Dim items = pick.Where(Function(f) f.Category = Cat.Achievement AndAlso f.Status = FindStatus.New).ToList()
            If achs Is Nothing OrElse items.Count = 0 Then Return
            Dim have = New HashSet(Of String)(achs.Select(Function(a) a.AsArray()(0).GetValue(Of String)()))
            For Each f In items
                If have.Contains(f.Id) Then Continue For
                achs.Add(New JsonArray(J(f.Id), J(NonEmpty(f.Name, f.Id)), J(f.Description), J(IconOf(f)), J(NonEmpty(f.Extra, "Feats"))))
            Next
            tb.Touch("ACHS")
            log.Add($"Achievements: added {items.Count}.")
        End Sub

        Private Sub AddZones(tb As Tables, pick As List(Of Finding), log As List(Of String))
            Dim items = pick.Where(Function(f) f.Category = Cat.Zone AndAlso f.Status = FindStatus.New).ToList()
            If items.Count = 0 Then Return
            Dim zones = tb.Get("ZONES").AsArray()
            Dim names = tb.Obj("ZONE_NAMES")
            For Each f In items
                If Not zones.Any(Function(z) z.GetValue(Of String)() = f.Id) Then
                    Dim at = -1
                    If f.Path.StartsWith("after:") Then
                        Dim prev = f.Path.Substring(6)
                        For i = 0 To zones.Count - 1
                            If zones(i).GetValue(Of String)() = prev Then at = i + 1
                        Next
                    End If
                    If at >= 0 AndAlso at < zones.Count Then zones.Insert(at, f.Id) Else zones.Add(f.Id)
                End If
                names(f.Id) = NonEmpty(f.Name, f.Id)
            Next
            tb.Touch("ZONES") : tb.Touch("ZONE_NAMES")
            log.Add($"Zones: added {String.Join(", ", items.Select(Function(z) z.Name))}.")
        End Sub

        Private Sub AddSimple(tb As Tables, pick As List(Of Finding), log As List(Of String))
            For Each f In pick.Where(Function(x) x.Status = FindStatus.New)
                Select Case f.Category
                    Case Cat.Pet
                        tb.Obj("PETS")(f.Id) = NonEmpty(f.Name, f.Id) : tb.Touch("PETS")
                        tb.Sub_("IC", "pet")(f.Id) = IconOf(f) : tb.Touch("IC")
                    Case Cat.Tabard
                        tb.Obj("TABARDS")(f.Id) = NonEmpty(f.Name, f.Id) : tb.Touch("TABARDS")
                        If Not String.IsNullOrWhiteSpace(f.Icon) AndAlso f.Icon <> "INV_Misc_QuestionMark" AndAlso tb.Obj("IC.tab") IsNot Nothing Then
                            tb.Obj("IC.tab")(f.Id) = f.Icon : tb.Touch("IC.tab")
                        End If
                    Case Cat.Perk
                        tb.Obj("PERKMAX")(f.Id) = ToInt(f.Extra, 1) : tb.Touch("PERKMAX")
                        tb.Sub_("IC", "p")(f.Id) = New JsonArray(J(IconOf(f)), J(NonEmpty(f.Name, f.Id))) : tb.Touch("IC")
                    Case Cat.Jewelry
                        tb.Sub_("CODEX", "J")(f.Id) = NonEmpty(f.Name, f.Id) : tb.Touch("CODEX")
                        tb.Sub_("IC", "j")(f.Id) = IconOf(f) : tb.Touch("IC")
                    Case Cat.Armory
                        tb.Sub_("CODEX", "A")(f.Id) = New JsonArray(J(NonEmpty(f.Name, f.Id)), JsonValue.Create(ToInt(f.Extra, 3))) : tb.Touch("CODEX")
                        tb.Sub_("IC", "a")(f.Id) = IconOf(f) : tb.Touch("IC")
                    Case Else
                        Continue For
                End Select
            Next
            For Each g In pick.Where(Function(x) x.Status = FindStatus.New).GroupBy(Function(x) x.Category)
                If {Cat.Pet, Cat.Tabard, Cat.Perk, Cat.Jewelry, Cat.Armory}.Contains(g.Key) Then log.Add($"{g.Key}: added {g.Count()}.")
            Next
        End Sub

        ' ---------- talents ----------

        Private Sub Talents(tb As Tables, known As KnownData, pick As List(Of Finding), log As List(Of String))
            Dim items = pick.Where(Function(f) f.Category = Cat.Talent AndAlso
                                       (f.Status = FindStatus.New OrElse f.Prop = "talent")).ToList()
            If items.Count = 0 Then Return
            Dim raw = tb.Get("TREES")
            If raw Is Nothing Then Return
            Dim trees = raw.GetValue(Of String)().Split("|"c).Select(Function(x) x.Split(","c, StringSplitOptions.RemoveEmptyEntries).ToList()).ToList()
            Dim added = 0, moved = 0
            For Each f In items
                Dim p = f.Extra.Split("|"c)
                If p.Length < 4 Then log.Add($"Talent {f.Id}: Extra must be tree|max|row|col, skipped.") : Continue For
                Dim tree = known.TreeIndex(p(0))
                Dim mx, rw, cl As Integer
                If tree < 0 OrElse tree >= trees.Count OrElse Not Integer.TryParse(p(1), mx) OrElse Not Integer.TryParse(p(2), rw) OrElse Not Integer.TryParse(p(3), cl) Then
                    log.Add($"Talent {f.Id}: could not read '{f.Extra}', skipped.") : Continue For
                End If
                For Each tr In trees : tr.RemoveAll(Function(x) x.Split(":"c)(0) = f.Id) : Next
                trees(tree).Add($"{f.Id}:{mx}:{rw}:{cl}")
                ' keep each tree ordered by row then column, like the game
                trees(tree) = trees(tree).OrderBy(Function(x) CInt(x.Split(":"c)(2))).ThenBy(Function(x) CInt(x.Split(":"c)(3))).ToList()
                If f.Status = FindStatus.New Then
                    added += 1
                    tb.Sub_("IC", "t")(f.Id) = New JsonArray(J(NonEmpty(f.Icon, "INV_Misc_QuestionMark")), J(NonEmpty(f.Name, f.Id))) : tb.Touch("IC")
                Else
                    moved += 1
                End If
            Next
            tb.Put("TREES", JsonValue.Create(String.Join("|", trees.Select(Function(x) String.Join(",", x)))))
            log.Add($"Talents: added {added}, moved/re-ranked {moved}.")
        End Sub

        ' ---------- codex legendary abilities ----------

        Private Sub Legendary(tb As Tables, rows As List(Of Finding), log As List(Of String))
            Dim byClass = rows.Where(Function(f) f.Data IsNot Nothing AndAlso f.Data.ContainsKey("order")).GroupBy(Function(f) f.Path)
            For Each g In byClass
                If Not g.Any(Function(f) f.Include) Then Continue For
                Dim cls = g.Key.Substring(2)
                Dim L = tb.Sub_("CODEX", "L")
                If L Is Nothing Then Continue For
                Dim old = TryCast(L(cls), JsonArray)
                Dim oldIds = If(old Is Nothing, New List(Of String), old.Select(Function(x) x.GetValue(Of String)()).ToList())
                Dim skipNew = g.Where(Function(f) f.Status = FindStatus.New AndAlso Not f.Include).Select(Function(f) f.Id).ToHashSet()
                Dim keepOld = g.Where(Function(f) f.Prop = "removed" AndAlso Not f.Include).Select(Function(f) f.Id).ToList()
                Dim ids = g.First().Data("order").Split(","c).Where(Function(x) Not skipNew.Contains(x)).ToList()
                ids.AddRange(keepOld.Where(Function(x) Not ids.Contains(x)))
                L(cls) = New JsonArray(ids.Select(Function(x) CType(J(x), JsonNode)).ToArray())
                tb.Touch("CODEX")
                For Each f In g.Where(Function(x) x.Status = FindStatus.New AndAlso x.Include)
                    Dim ab = tb.Obj("IC.ab")
                    If ab IsNot Nothing Then ab(f.Id) = New JsonArray(J(IconOf(f)), J(NonEmpty(f.Name, f.Id))) : tb.Touch("IC.ab")
                Next
                log.Add($"Codex legendary ({g.First().Extra}): " & String.Join(", ", ids))
            Next
        End Sub

        ' ---------- changed values ----------

        Private Sub ChangedValues(tb As Tables, pick As List(Of Finding), log As List(Of String))
            Dim n = 0
            For Each f In pick.Where(Function(x) x.Status = FindStatus.Changed AndAlso x.Prop <> "talent" AndAlso x.Category <> Cat.Gear)
                Dim v = Value(f)
                If String.IsNullOrWhiteSpace(v) Then Continue For
                Select Case f.Category
                    Case Cat.Achievement
                        Dim achs = TryCast(tb.Get("ACHS"), JsonArray)
                        Dim a = achs?.FirstOrDefault(Function(x) x.AsArray()(0).GetValue(Of String)() = f.Id)
                        If a Is Nothing Then Continue For
                        Dim idx = If(f.Prop = "name", 1, If(f.Prop = "desc", 2, If(f.Prop = "icon", 3, -1)))
                        If idx < 0 Then Continue For
                        a.AsArray()(idx) = v : tb.Touch("ACHS")
                    Case Cat.Pet
                        If f.Prop = "icon" Then SetIn(tb, "IC", "pet", f.Id, v) Else If f.Prop = "name" Then SetTop(tb, "PETS", f.Id, v)
                    Case Cat.Tabard
                        If f.Prop = "icon" Then SetTop(tb, "IC.tab", f.Id, v) Else If f.Prop = "name" Then SetTop(tb, "TABARDS", f.Id, v)
                    Case Cat.Jewelry
                        If f.Prop = "icon" Then SetIn(tb, "IC", "j", f.Id, v) Else If f.Prop = "name" Then SetIn(tb, "CODEX", "J", f.Id, v)
                    Case Cat.Armory
                        If f.Prop = "icon" Then
                            SetIn(tb, "IC", "a", f.Id, v)
                        Else
                            Dim a = TryCast(tb.Sub_("CODEX", "A")(f.Id), JsonArray)
                            If a Is Nothing Then Continue For
                            If f.Prop = "name" Then a(0) = v
                            If f.Prop = "rarity" Then a(1) = ToInt(v, 3)
                            tb.Touch("CODEX")
                        End If
                    Case Cat.Perk
                        If f.Prop = "max" Then
                            tb.Obj("PERKMAX")(f.Id) = ToInt(v, 1) : tb.Touch("PERKMAX")
                        Else
                            SetPair(tb, tb.Sub_("IC", "p"), "IC", f.Id, f.Prop, v)
                        End If
                    Case Cat.Talent
                        SetPair(tb, tb.Sub_("IC", "t"), "IC", f.Id, f.Prop, v)
                    Case Cat.Ability
                        SetPair(tb, tb.Obj("IC.ab"), "IC.ab", f.Id, f.Prop, v)
                    Case Else
                        Continue For
                End Select
                n += 1
            Next
            If n > 0 Then log.Add($"Updated {n} changed value(s) (icons, names, ranks).")
        End Sub

        ''' <summary>The edited value of a Changed row, from the column that holds it.</summary>
        Private Function Value(f As Finding) As String
            Select Case f.Prop
                Case "icon" : Return f.Icon
                Case "name" : Return f.Name
                Case "desc" : Return f.Description
                Case Else : Return f.Extra
            End Select
        End Function

        Private Sub SetTop(tb As Tables, table As String, id As String, v As String)
            Dim o = tb.Obj(table)
            If o Is Nothing Then Return
            o(id) = v : tb.Touch(table)
        End Sub

        Private Sub SetIn(tb As Tables, table As String, key As String, id As String, v As String)
            Dim o = tb.Sub_(table, key)
            If o Is Nothing Then Return
            o(id) = v : tb.Touch(table)
        End Sub

        ''' <summary>IC.t / IC.p / IC.ab entries are [icon, name].</summary>
        Private Sub SetPair(tb As Tables, o As JsonObject, table As String, id As String, prop As String, v As String)
            If o Is Nothing Then Return
            Dim a = TryCast(o(id), JsonArray)
            If a Is Nothing Then a = New JsonArray(J("INV_Misc_QuestionMark"), J(id)) : o(id) = a
            If prop = "icon" Then a(0) = v
            If prop = "name" Then a(1) = v
            tb.Touch(table)
        End Sub

        ' ---------- gear ----------

        Private ReadOnly NewTierColours As String() = {"#00ccff", "#e6cc80", "#ff4fd8", "#7cfc00"}

        Private Sub Gear(t As EditorTemplate, tb As Tables, pick As List(Of Finding), log As List(Of String))
            Dim items = pick.Where(Function(f) f.Category = Cat.Gear).ToList()
            If items.Count = 0 Then Return
            Dim g = tb.Obj("GEAR")
            If g Is Nothing Then Return
            Dim renamed = 0, added = 0
            Dim newTiers As New SortedSet(Of Integer)
            For Each f In items.OrderBy(Function(x) x.Path)
                If f.Path = "icons" Then
                    If f.Data Is Nothing Then Continue For
                    Dim gi = tb.Sub_("IC", "g")
                    For Each kv In f.Data : gi(kv.Key) = kv.Value : Next
                    tb.Touch("IC")
                    log.Add($"Gear: {f.Data.Count} item icons added.")
                    Continue For
                End If
                Dim parts = f.Path.Split("/"c)
                Dim list As JsonArray = Nothing
                Select Case parts(0)
                    Case "r" : list = TryCast(g("r"), JsonArray)
                    Case "s", "n", "pw" : list = TryCast(g(parts(0))?(parts(1)), JsonArray)
                    Case "w" : list = TryCast(TryCast(g("w")?(parts(1)), JsonArray)?(CInt(parts(2))), JsonArray)
                End Select
                If list Is Nothing Then Continue For
                Dim idx = CInt(parts(parts.Length - 1))
                If parts(0) = "n" AndAlso idx >= 8 Then Continue For      ' no room for more slots in the item ids
                While list.Count <= idx : list.Add(CType(Nothing, JsonNode)) : End While
                Dim isNew = list(idx) Is Nothing
                list(idx) = NonEmpty(f.Name, "?")
                If f.Status = FindStatus.Changed Then renamed += 1 Else added += 1
                If (parts(0) = "s" OrElse parts(0) = "w") AndAlso idx >= 5 Then newTiers.Add(ExeScanner.TierOfIndex(idx))
                If parts(0) = "r" AndAlso idx >= 6 Then newTiers.Add(idx)
            Next
            tb.Touch("GEAR")

            If newTiers.Count > 0 Then
                Dim rar = TryCast(g("r"), JsonArray)
                Dim order = tb.Get("TIER_ORDER").AsArray()
                Dim col = tb.Get("TCOL").AsArray()
                For Each tier In newTiers
                    While rar.Count <= tier : rar.Add(J("Tier " & (rar.Count + 1))) : End While
                    If Not order.Any(Function(x) x.GetValue(Of Integer)() = tier) Then order.Add(tier)
                    While col.Count <= tier : col.Add(J(NewTierColours((col.Count - 6) Mod NewTierColours.Length))) : End While
                Next
                ' every class needs a name for the new tier, even if the game file only showed some of them
                For Each tier In newTiers
                    Dim li = If(tier > 5, tier - 1, tier)
                    For Each kv In If(TryCast(g("s"), JsonObject), New JsonObject())
                        Dim l = TryCast(kv.Value, JsonArray)
                        If l Is Nothing Then Continue For
                        While l.Count <= li : l.Add(CType(Nothing, JsonNode)) : End While
                        If l(li) Is Nothing Then l(li) = rar(tier).ToString()
                    Next
                    For Each kv In If(TryCast(g("w"), JsonObject), New JsonObject())
                        For Each wl In If(TryCast(kv.Value, JsonArray), New JsonArray())
                            Dim l = TryCast(wl, JsonArray)
                            If l Is Nothing Then Continue For
                            While l.Count <= li : l.Add(CType(Nothing, JsonNode)) : End While
                            If l(li) Is Nothing Then l(li) = rar(tier).ToString() & " weapon"
                        Next
                    Next
                Next
                tb.Touch("TIER_ORDER") : tb.Touch("TCOL")
                log.Add("Gear: new tier(s) " & String.Join(", ", newTiers.Select(Function(x) rar(x).ToString())) & " added to the Gear tab.")
            End If
            If renamed + added > 0 Then log.Add($"Gear: {renamed} name(s) updated, {added} added.")
        End Sub

        ' ---------- editor code that has to be data-driven for new gear ----------

        Private ReadOnly CodePatches As (Find As String, Repl As String)() = {
            ("for(let t=0;t<=4;t++)have.add(gearId(c,s,t))", "TIER_ORDER.filter(t=>t!==5).forEach(t=>have.add(gearId(c,s,t)))"),
            ("GEAR.s[k][t]+"" ""+n", "GEAR.s[k][TI(t)]+"" ""+n"),
            ("[s-8]||[])[t]||""""", "[s-8]||[])[TI(t)]||"""""),
            ("${glyph(s)}<div class=""gn"">", "${IC.g&&IC.g[id]?ico(IC.g[id]):glyph(s)}<div class=""gn"">")}

        ''' <summary>Makes the Gear tab follow the data (extra tiers, item icons). Safe to run on an already patched page.</summary>
        Public Sub EnsureCodePatches(t As EditorTemplate)
            Dim h = t.Html
            For Each p In CodePatches
                h = h.Replace(p.Find, p.Repl)
            Next
            If Not h.Contains("const TI=") Then
                Dim i = h.IndexOf(vbLf & "const SLOT_NAMES=")
                If i >= 0 Then h = h.Insert(i + 1, "const TI=t=>t>5?t-1:t; // list position of a gear tier (tier 5 is PvP)" & vbLf)
            End If
            t.Html = h
        End Sub
    End Module

End Namespace
