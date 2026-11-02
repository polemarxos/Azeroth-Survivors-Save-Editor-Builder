Imports System.IO
Imports System.Text
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>
    ''' Gear pass. The editor builds gear names from lists (set name per class and tier, slot names per armour type,
    ''' weapon names per class, rarity names). Each list is looked up in the game file as a run of strings in the
    ''' same order; a string that differs inside the run is a renamed item, and extra strings right after it are new
    ''' entries (for example a new tier). If the game also stores item ids (1CCST0), new ids and item icons are read too.
    ''' </summary>
    Partial Public Class ExeScanner

        Private textIndex As Dictionary(Of String, List(Of Integer))

        Private Function FieldsWithText(t As String) As List(Of Integer)
            If textIndex Is Nothing Then
                textIndex = New Dictionary(Of String, List(Of Integer))(StringComparer.Ordinal)
                For i = 0 To Fields.Count - 1
                    Dim l As List(Of Integer) = Nothing
                    If Not textIndex.TryGetValue(Fields(i).Text, l) Then l = New List(Of Integer) : textIndex(Fields(i).Text) = l
                    If l.Count < 200 Then l.Add(i)
                Next
            End If
            Dim r As List(Of Integer) = Nothing
            Return If(textIndex.TryGetValue(t, r), r, New List(Of Integer))
        End Function

        Private Class GearList
            Public Path As String
            Public Items As List(Of String)   ' Nothing = empty slot in the editor's list
            Public Label As Func(Of Integer, String)
            Public CanExtend As Boolean
        End Class

        Public Shared ReadOnly RarityDefault As String() = {"Common", "Uncommon", "Rare", "Epic", "Legendary", "PvP"}

        ''' <summary>Editor tier number for a position in a set/weapon list (tier 5 is PvP, so positions 5+ become tiers 6+).</summary>
        Public Shared Function TierOfIndex(i As Integer) As Integer
            Return If(i < 5, i, i + 1)
        End Function

        Private Shared Function Arr(n As JsonNode) As List(Of String)
            Dim a = TryCast(n, JsonArray)
            If a Is Nothing Then Return Nothing
            Return a.Select(Function(x) If(x Is Nothing, Nothing, x.ToString())).ToList()
        End Function

        Private Function GearLists(known As KnownData) As List(Of GearList)
            Dim out As New List(Of GearList)
            Dim g = known.Gear
            If g Is Nothing Then Return out
            Dim rar = If(Arr(g("r")), RarityDefault.ToList())
            Dim tierName = Function(i As Integer) As String
                               Dim t = TierOfIndex(i)
                               Return If(t < rar.Count, rar(t) & " tier", "new tier " & (t + 1))
                           End Function
            Dim cap = Function(s As String) If(s.Length > 0, Char.ToUpper(s(0)) & s.Substring(1), s)
            If rar IsNot Nothing Then out.Add(New GearList With {.Path = "r", .Items = rar, .CanExtend = True, .Label = Function(i) "Rarity name #" & (i + 1)})
            For Each cls In If(TryCast(g("s"), JsonObject), New JsonObject())
                Dim c = cls.Key
                out.Add(New GearList With {.Path = "s/" & c, .Items = Arr(cls.Value), .CanExtend = True,
                        .Label = Function(i) $"{cap(c)} armour set, {tierName(i)}"})
            Next
            For Each ty In If(TryCast(g("n"), JsonObject), New JsonObject())
                Dim tn = ty.Key
                out.Add(New GearList With {.Path = "n/" & tn, .Items = Arr(ty.Value), .CanExtend = True,
                        .Label = Function(i) $"{cap(tn)} slot #{i + 1}"})
            Next
            For Each cls In If(TryCast(g("w"), JsonObject), New JsonObject())
                Dim c = cls.Key
                Dim lists = TryCast(cls.Value, JsonArray)
                If lists Is Nothing Then Continue For
                For k = 0 To lists.Count - 1
                    Dim kk = k
                    out.Add(New GearList With {.Path = $"w/{c}/{k}", .Items = Arr(lists(k)), .CanExtend = True,
                            .Label = Function(i) $"{cap(c)} {If(kk = 0, "main hand", "off hand")}, {tierName(i)}"})
                Next
            Next
            For Each cls In If(TryCast(g("pw"), JsonObject), New JsonObject())
                Dim c = cls.Key
                out.Add(New GearList With {.Path = "pw/" & c, .Items = Arr(cls.Value), .CanExtend = False,
                        .Label = Function(i) $"{cap(c)} PvP weapon ({If(i = 0, "Alliance", "Horde")})"})
            Next
            Return out.Where(Function(l) l.Items IsNot Nothing).ToList()
        End Function

        Private Function GearScan(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim lists = GearLists(known)
            If lists.Count = 0 Then Return out
            Dim allNames As New HashSet(Of String)(lists.SelectMany(Function(l) l.Items).Where(Function(x) x IsNot Nothing))
            Dim located = 0
            For Each gl In lists
                Dim res = AlignList(gl.Items)
                If res Is Nothing Then Continue For
                located += 1
                Dim pos = res.Value.Pos
                Dim gap = res.Value.Gap
                ' renamed items: an unmatched item sitting exactly between two matched neighbours
                For i = 0 To gl.Items.Count - 1
                    If pos(i) >= 0 OrElse gl.Items(i) Is Nothing Then Continue For
                    Dim cand = -1
                    If i > 0 AndAlso pos(i - 1) >= 0 AndAlso (i = gl.Items.Count - 1 OrElse (pos(i + 1) >= 0 AndAlso pos(i + 1) = pos(i - 1) + 2)) Then
                        cand = pos(i - 1) + 1
                    ElseIf i = 0 AndAlso gl.Items.Count > 1 AndAlso pos(1) > 0 Then
                        cand = pos(1) - 1
                    End If
                    If cand < 0 OrElse cand >= Fields.Count Then Continue For
                    Dim t = Fields(cand).Text
                    If Not IsTitle(t) OrElse allNames.Contains(t) Then Continue For
                    out.Add(New Finding With {.Category = Cat.Gear, .Status = FindStatus.Changed, .Prop = "gear", .Path = $"{gl.Path}/{i}",
                        .Id = gl.Label(i), .Name = t, .Offset = Fields(cand).Offset, .Notes = "was: " & gl.Items(i)})
                Next
                ' new entries right after the list
                If Not gl.CanExtend Then Continue For
                Dim last = pos.Max()
                If pos(gl.Items.Count - 1) < 0 AndAlso gl.Items(gl.Items.Count - 1) IsNot Nothing Then
                    ' the last item was renamed: continue after it
                    If out.Any(Function(f) f.Path = $"{gl.Path}/{gl.Items.Count - 1}") Then last += 1 Else Continue For
                End If
                Dim k = 0
                Dim prevOff = Fields(last).Offset
                For j = last + 1 To Math.Min(Fields.Count - 1, last + 6)
                    Dim f = Fields(j)
                    If f.Offset - prevOff > gap * 1.5 + 8 Then Exit For
                    If Not IsTitle(f.Text) OrElse allNames.Contains(f.Text) OrElse f.Text.Length > 48 Then Exit For
                    Dim idx = gl.Items.Count + k
                    Dim fd As New Finding With {.Category = Cat.Gear, .Prop = "gear", .Path = $"{gl.Path}/{idx}", .Id = gl.Label(idx), .Name = f.Text, .Offset = f.Offset}
                    If gl.Path.StartsWith("n/") Then
                        fd.Include = False
                        fd.Notes = "new armour slot: the editor's item ids only have room for 10 slots, so it is listed only"
                    ElseIf gl.Path = "r" Then
                        fd.Notes = "new rarity / tier name"
                    ElseIf idx < 5 Then
                        fd.Notes = "name for a tier the editor already has"
                    Else
                        fd.Notes = $"new tier (tier {TierOfIndex(idx) + 1}); item ids assumed to follow the same pattern"
                    End If
                    out.Add(fd)
                    prevOff = f.Offset
                    k += 1
                Next
            Next
            Log($"Gear: {located} of {lists.Count} name lists found in the game file; {out.Where(Function(f) f.Status = FindStatus.Changed).Count()} renamed, {out.Where(Function(f) f.Status = FindStatus.New).Count()} new.")
            If PeMode Then out.AddRange(GearIconFindings(known)) Else out.AddRange(GearItems(known))
            Return out
        End Function

        ''' <summary>Finds the list as a run of strings in the same order. Returns the field index of each item (-1 = not matched).</summary>
        Private Function AlignList(items As List(Of String)) As (Pos As Integer(), Gap As Double)?
            Dim n = items.Count
            Dim real = items.Where(Function(x) x IsNot Nothing).Count()
            If real < 2 Then Return Nothing
            Dim starts As New HashSet(Of Integer)
            For i = 0 To n - 1
                If items(i) Is Nothing Then Continue For
                For Each p In FieldsWithText(items(i))
                    If p - i >= 0 Then starts.Add(p - i)
                Next
            Next
            Dim best As Integer() = Nothing, bestScore = 0
            For Each st In starts
                Dim pos(n - 1) As Integer
                Dim k = st, score = 0
                For i = 0 To n - 1
                    pos(i) = -1
                    If items(i) Is Nothing Then k += 1 : Continue For
                    Dim hit = -1
                    For d = 0 To 2
                        If k + d < Fields.Count AndAlso Fields(k + d).Text = items(i) Then hit = k + d : Exit For
                    Next
                    If hit >= 0 Then
                        pos(i) = hit : score += 1 : k = hit + 1
                    Else
                        k += 1
                    End If
                Next
                If score > bestScore Then bestScore = score : best = pos
            Next
            If best Is Nothing OrElse bestScore < Math.Max(2, CInt(Math.Ceiling(real * 0.6))) Then Return Nothing
            ' typical distance between neighbouring items
            Dim gaps As New List(Of Long)
            For i = 1 To n - 1
                If best(i) >= 0 AndAlso best(i - 1) >= 0 AndAlso best(i) = best(i - 1) + 1 Then gaps.Add(Fields(best(i)).Offset - Fields(best(i - 1)).Offset)
            Next
            If gaps.Count = 0 Then Return Nothing
            gaps.Sort()
            Dim span = Fields(best.Where(Function(x) x >= 0).Max()).Offset - Fields(best.Where(Function(x) x >= 0).Min()).Offset
            If span > 200L * n Then Return Nothing
            Return (best, CDbl(gaps(gaps.Count \ 2)))
        End Function

        ''' <summary>If the game file lists item ids (1CCST0), report ids the editor doesn't know and collect item icons.</summary>
        Private Function GearItems(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim cls = New HashSet(Of Integer)(known.ClsOrder)
            Dim editorIds As New HashSet(Of Integer)
            For Each c In known.ClsOrder
                Dim off = c = 4 OrElse c = 7
                For s = 0 To If(off, 9, 8)
                    For t = 0 To 4 : editorIds.Add(100000 + c * 1000 + s * 100 + t * 10) : Next
                Next
                For s = 0 To If(off, 9, 8) : editorIds.Add(100000 + c * 1000 + s * 100 + 50) : Next
            Next
            Dim hits As New List(Of (Id As Integer, Off As Long))
            For Each kv In Words
                Dim w = kv.Key
                If w.Length <> 6 OrElse w(0) <> "1"c OrElse Not Char.IsDigit(w(1)) Then Continue For
                Dim id As Integer
                If Not Integer.TryParse(w, id) OrElse id Mod 10 <> 0 Then Continue For
                If Not cls.Contains((id \ 1000) Mod 100) Then Continue For
                Dim o = kv.Value.FirstOrDefault(Function(x) x.Alone)
                If o.Off = 0 AndAlso Not kv.Value.Any(Function(x) x.Alone) Then Continue For
                hits.Add((id, o.Off))
            Next
            Dim knownHits = hits.Where(Function(h) editorIds.Contains(h.Id)).Count()
            If knownHits < 20 Then
                Log("Gear item ids: not stored in the game file (" & knownHits & " found), skipped.")
                Return out
            End If
            hits.Sort(Function(a, b) a.Off.CompareTo(b.Off))
            Dim icons As New Dictionary(Of String, String)
            Dim unknown As New List(Of Integer)
            For i = 0 To hits.Count - 1
                Dim lim = If(i + 1 < hits.Count, hits(i + 1).Off, hits(i).Off + 700)
                Dim tk = EntryTokens(hits(i).Off, hits(i).Id.ToString(), lim)
                Dim ic = tk.FirstOrDefault(Function(x) IsIcon(x.Value)).Value
                If ic IsNot Nothing Then icons(hits(i).Id.ToString()) = ic
                If Not editorIds.Contains(hits(i).Id) Then unknown.Add(hits(i).Id)
            Next
            ' one row per new tier or new slot, not one per item
            For Each grp In unknown.GroupBy(Function(id) If((id \ 10) Mod 10 > 5, "t" & (id \ 10) Mod 10, "s" & (id \ 100) Mod 10))
                Dim ids = grp.OrderBy(Function(x) x).ToList()
                Dim classes = ids.Select(Function(x) (x \ 1000) Mod 100).Distinct().Select(Function(c) KnownData.ClassNames.GetValueOrDefault(c, "class " & c))
                Dim what = If(grp.Key(0) = "t"c, "tier " & (CInt(grp.Key.Substring(1)) + 1), "slot " & (CInt(grp.Key.Substring(1)) + 1))
                out.Add(New Finding With {.Category = Cat.GearItem, .Status = FindStatus.Info, .Include = False, .Id = $"{ids.First()}…{ids.Last()}",
                    .Name = $"{ids.Count} items in {what}", .Icon = icons.GetValueOrDefault(ids.First().ToString(), ""), .Offset = -1,
                    .Notes = $"{what} for {String.Join(", ", classes)}" &
                             If(grp.Key(0) = "t"c, " - added to the Gear tab when the matching new tier name is ticked", " - the editor has no such slot yet")})
            Next
            Log($"Gear item ids: {knownHits} known, {unknown.Count} unknown; icons for {icons.Count}.")
            If icons.Count >= knownHits * 0.7 Then
                out.Insert(0, New Finding With {.Category = Cat.Gear, .Prop = "icons", .Path = "icons", .Id = "Item icons",
                    .Name = $"{icons.Count} gear icons", .Icon = icons.Values.First(), .Data = icons,
                    .Notes = "shows the game's item icons on the Gear tab instead of the slot drawings"})
            End If
            Return out
        End Function
        ''' <summary>WoW 1.12 folder (or a folder with ItemDisplayInfo.dbc) for gear icons; empty = look next to the game.</summary>
        Public Property WowFolder As String = ""
        Private exeFile As String = ""

        Private Shared ReadOnly SlotWords As String()() = {
            New String() {"helm", "helmet", "crown", "mask", "hat", "cap", "hood", "circlet"}, New String() {"shoulder"}, New String() {"chest", "robe", "shirt", "tunic"},
            New String() {"belt", "girdle"}, New String() {"pant", "leg"}, New String() {"boot"}, New String() {"bracer", "wrist"}, New String() {"gauntlet", "glove"}}

        ''' <summary>Where a list of name strings sits as an array of (pointer, length[, tag]) records. Returns (start, stride) or (-1, 0).</summary>
        Private Function FindNameArray(items As List(Of String)) As (Start As Integer, Stride As Integer)
            Dim i0 = items.FindIndex(Function(x) x IsNot Nothing)
            If i0 < 0 Then Return (-1, 0)
            For Each fi In FieldsWithText(items(i0))
                Dim pl As List(Of Integer) = Nothing
                If Not PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then Continue For
                For Each p In pl
                    For Each stride In {16, 24, 32}
                        Dim st = p - stride * i0
                        If st < 0 Then Continue For
                        Dim ok = True
                        For k = 0 To items.Count - 1
                            If items(k) Is Nothing Then Continue For
                            If StrAt(st + stride * k) <> items(k) Then ok = False : Exit For
                        Next
                        If ok Then Return (st, stride)
                    Next
                Next
            Next
            Return (-1, 0)
        End Function

        ''' <summary>The string a (pointer, length) pair at file offset p points to, or Nothing.</summary>
        Private Function StrAt(p As Integer) As String
            If p < 0 OrElse p + 16 > Data.Length Then Return Nothing
            Dim v = BitConverter.ToInt64(Data, p), l = BitConverter.ToInt64(Data, p + 8)
            If v < rdLo OrElse v >= rdHi OrElse l < 1 OrElse l > 600 Then Return Nothing
            Dim o = VaToOff(v)
            If Not Printable(o, CInt(l)) Then Return Nothing
            Return Encoding.UTF8.GetString(Data, CInt(o), CInt(l))
        End Function

        Private Function IsPtr(p As Integer) As Boolean
            If p < 0 OrElse p + 8 > Data.Length Then Return False
            Dim v = BitConverter.ToInt64(Data, p)
            Return v >= ImageBase AndAlso v < ImageBase + &H20000000L
        End Function

        Private Function U32s(p As Integer, n As Integer) As Integer()
            Dim r(n - 1) As Integer
            For k = 0 To n - 1 : r(k) = BitConverter.ToInt32(Data, p + 4 * k) : Next
            Return r
        End Function

        ''' <summary>
        ''' The game keeps a WoW display id for every gear piece (this is what the game turns into a picture):
        '''  - class record: 5 set names, weapon list pointer + count, then 5 tiers x 8 slots of display ids;
        '''  - weapon record: 5 names, then 5 display ids;
        '''  - PvP record: Alliance/Horde prefixes, weapon list pointer + count, Alliance and Horde rows of 8.
        ''' An empty (0) armour id is filled the way the game does it: same slot, lower tiers first, then higher;
        ''' an empty PvP id uses the regular tier-4 (index 3) piece. Returns editor item id -> display id.
        ''' </summary>
        Private Function GearDisplayIds(known As KnownData) As Dictionary(Of Integer, Integer)
            Dim res As New Dictionary(Of Integer, Integer)
            If Not PeMode OrElse known.Gear Is Nothing Then Return res
            Dim clsId = Function(key As String) KnownData.ClassNames.FirstOrDefault(Function(p) p.Value.ToLowerInvariant().Replace(" ", "") = key).Key
            Dim rowsOf As New Dictionary(Of Integer, Integer()())   ' class -> tier -> 8 slots
            Dim lists = GearLists(known)

            ' regular armour
            For Each gl In lists.Where(Function(x) x.Path.StartsWith("s/"))
                Dim c = clsId(gl.Path.Split("/"c)(1))
                If c = 0 Then Continue For
                Dim arr = FindNameArray(gl.Items)
                If arr.Start < 0 Then Continue For
                Dim afterNames = arr.Start + arr.Stride * gl.Items.Count
                Dim at = If(IsPtr(afterNames), afterNames + 16, -1)          ' weapon list pointer + count, then the rows
                If at < 0 Then Continue For
                Dim v = U32s(at, 40)
                If v.Any(Function(x) x < 0 OrElse x > 200000) OrElse v.Count(Function(x) x > 0) < 20 Then Continue For
                rowsOf(c) = Enumerable.Range(0, 5).Select(Function(t) v.Skip(t * 8).Take(8).ToArray()).ToArray()
            Next
            Dim pick = Function(c As Integer, t As Integer, s As Integer) As Integer
                           Dim rows As Integer()() = Nothing
                           If Not rowsOf.TryGetValue(c, rows) Then Return 0
                           For tt = t To 0 Step -1
                               If rows(tt)(s) > 0 Then Return rows(tt)(s)
                           Next
                           For tt = t + 1 To 4
                               If rows(tt)(s) > 0 Then Return rows(tt)(s)
                           Next
                           Return 0
                       End Function
            For Each c In rowsOf.Keys
                For t = 0 To 4
                    For s = 0 To 7
                        Dim d = pick(c, t, s)
                        If d > 0 Then res(100000 + c * 1000 + s * 100 + t * 10) = d
                    Next
                Next
            Next

            ' regular weapons: record = 8 bytes, names, ..., display ids at +120
            For Each gl In lists.Where(Function(x) x.Path.StartsWith("w/"))
                Dim parts = gl.Path.Split("/"c)
                Dim c = clsId(parts(1))
                If c = 0 Then Continue For
                Dim arr = FindNameArray(gl.Items)
                If arr.Start < 0 Then Continue For
                Dim v = U32s(arr.Start - 8 + 120, gl.Items.Count)
                If v.Any(Function(x) x <= 0 OrElse x > 200000) Then Continue For
                Dim slot = 8 + CInt(parts(2))
                For t = 0 To Math.Min(4, v.Length - 1) : res(100000 + c * 1000 + slot * 100 + t * 10) = v(t) : Next
            Next

            ' PvP: find each class's PvP record through its PvP weapon record
            Dim wanted As New Dictionary(Of Long, HashSet(Of Integer))   ' weapon record VA -> classes (several classes share weapon names)
            For Each gl In lists.Where(Function(x) x.Path.StartsWith("pw/"))
                Dim c = clsId(gl.Path.Split("/"c)(1))
                If c = 0 Then Continue For
                Dim k = gl.Items.FindIndex(Function(x) x IsNot Nothing)
                If k < 0 Then Continue For
                For Each fi In FieldsWithText(gl.Items(k))
                    Dim pl As List(Of Integer) = Nothing
                    If Not PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then Continue For
                    For Each p In pl
                        Dim wrec = p - If(k = 0, 8, 32)                 ' weapon record: [display, name] x 2 factions
                        Dim va = OffToVa(wrec)
                        If va > 0 Then
                            If Not wanted.ContainsKey(va) Then wanted(va) = New HashSet(Of Integer)
                            wanted(va).Add(c)
                        End If
                    Next
                Next
            Next
            If wanted.Count > 0 Then
                For Each sec In Secs.Where(Function(x) x.Name = ".rdata" OrElse x.Name = ".data")
                    Dim e = CInt(sec.Raw + sec.RSize - 8)
                    For q = CInt(sec.Raw) To e Step 8
                        Dim cs As HashSet(Of Integer) = Nothing
                        If Not wanted.TryGetValue(BitConverter.ToInt64(Data, q), cs) Then Continue For
                        Dim rec = q - 32
                        If rec < 0 Then Continue For
                        Dim c = BitConverter.ToInt32(Data, rec + 112)
                        If Not cs.Contains(c) Then Continue For
                        Dim ally = U32s(rec + 48, 8), horde = U32s(rec + 80, 8)
                        For s = 0 To 7
                            Dim d = If(ally(s) > 0, ally(s), If(horde(s) > 0, horde(s), pick(c, 3, s)))
                            If d > 0 AndAlso d <= 200000 Then res(100000 + c * 1000 + s * 100 + 50) = d
                        Next
                        Dim wptr = BitConverter.ToInt64(Data, rec + 32), wcount = BitConverter.ToInt64(Data, rec + 40)
                        Dim wo = CInt(VaToOff(wptr))
                        For k = 0 To CInt(Math.Min(wcount, 2)) - 1
                            Dim a = BitConverter.ToInt32(Data, wo + 48 * k), h = BitConverter.ToInt32(Data, wo + 48 * k + 24)
                            Dim d = If(a > 0, a, h)
                            If d > 0 AndAlso d <= 200000 Then res(100000 + c * 1000 + (8 + k) * 100 + 50) = d
                        Next
                    Next
                Next
            End If
            Return res
        End Function

        ''' <summary>display id -> icon for every gear piece the game has, bundled with this program (no WoW client needed).</summary>
        Private Shared Function BundledGearIcons() As Dictionary(Of Integer, String)
            Dim d As New Dictionary(Of Integer, String)
            Dim asm = Reflection.Assembly.GetExecutingAssembly()
            Dim name = asm.GetManifestResourceNames().FirstOrDefault(Function(n) n.EndsWith("gear-icons.json", StringComparison.OrdinalIgnoreCase))
            If name Is Nothing Then Return d
            Using s = asm.GetManifestResourceStream(name)
                Dim o = Text.Json.Nodes.JsonNode.Parse(s).AsObject()
                For Each kv In o : d(CInt(kv.Key)) = kv.Value.GetValue(Of String)() : Next
            End Using
            Return d
        End Function

        ''' <summary>Gear icons: the game's display ids, turned into icons with the bundled table (and the WoW client, if given, for anything newer).</summary>
        Private Function GearIconFindings(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim disp = GearDisplayIds(known)
            If disp.Count = 0 Then Return out
            Log($"Gear: display ids for {disp.Count} gear pieces found in the game.")
            Dim table = BundledGearIcons()
            Dim missing = disp.Values.Distinct().Where(Function(x) Not table.ContainsKey(x)).ToList()
            If missing.Count > 0 Then
                ' newer pieces: try the WoW client if there is one
                For Each cand In {WowFolder, exeFile}
                    If String.IsNullOrWhiteSpace(cand) Then Continue For
                    Dim wow As WowData = Nothing
                    Try
                        wow = WowData.Load(cand, Log)
                    Catch ex As Exception
                        Log("  WoW data: " & ex.Message)
                    End Try
                    If wow Is Nothing Then Continue For
                    For Each m In missing
                        Dim ic As String = Nothing
                        If wow.Icons.TryGetValue(m, ic) Then table(m) = ic
                    Next
                    Log($"  icons for newer pieces from {wow.Source}.")
                    Exit For
                Next
            End If
            Dim icons As New Dictionary(Of String, String)
            For Each kv In disp
                Dim ic As String = Nothing
                If table.TryGetValue(kv.Value, ic) Then icons(kv.Key.ToString()) = ic
            Next
            Dim noIcon = disp.Where(Function(kv) Not table.ContainsKey(kv.Value)).ToList()
            Log($"  {icons.Count} of {disp.Count} gear pieces have an icon.")
            If icons.Count > 0 Then
                out.Add(New Finding With {.Category = Cat.Gear, .Prop = "icons", .Path = "icons", .Id = "Item icons",
                    .Name = $"{icons.Count} gear icons", .Icon = icons.Values.First(), .Data = icons,
                    .Notes = "the game's own item icons on the Gear tab instead of the slot drawings"})
            End If
            If noIcon.Count > 0 Then
                out.Add(New Finding With {.Category = Cat.GearItem, .Status = FindStatus.Info, .Include = False, .Id = "New gear pieces",
                    .Name = $"{noIcon.Count} pieces without an icon",
                    .Notes = "display ids " & String.Join(", ", noIcon.Select(Function(x) x.Value).Distinct().Take(12)) &
                             " are newer than the built-in icon list; pick your WoW 1.12 folder on the Scan tab to get them, or send the scan report"})
            End If
            Return out
        End Function
    End Class

End Namespace
