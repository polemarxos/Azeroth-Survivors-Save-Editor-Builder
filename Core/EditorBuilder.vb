Imports System.IO
Imports System.Text.Json.Nodes

Namespace Core

    ''' <summary>Writes the ticked "New" findings (and a changed seal key) into the editor's data tables.</summary>
    Public Module EditorBuilder

        Public Function Apply(t As EditorTemplate, findings As IEnumerable(Of Finding), key As KeyResult) As List(Of String)
            Dim log As New List(Of String)
            Dim pick = findings.Where(Function(f) f.Include AndAlso f.Status = FindStatus.New AndAlso Cat.Writable.Contains(f.Category)).ToList()

            ' achievements
            Dim achs = TryCast(t.GetJson("ACHS"), JsonArray)
            Dim newAch = pick.Where(Function(f) f.Category = Cat.Achievement).ToList()
            If achs IsNot Nothing AndAlso newAch.Count > 0 Then
                Dim have = New HashSet(Of String)(achs.Select(Function(a) a.AsArray()(0).GetValue(Of String)()))
                For Each f In newAch
                    If have.Contains(f.Id) Then Continue For
                    achs.Add(New JsonArray(JsonValue.Create(f.Id), JsonValue.Create(NonEmpty(f.Name, f.Id)), JsonValue.Create(f.Description),
                                           JsonValue.Create(NonEmpty(f.Icon, "INV_Misc_QuestionMark")), JsonValue.Create(NonEmpty(f.Extra, "Feats"))))
                Next
                t.SetJson("ACHS", achs)
                log.Add($"Achievements: added {newAch.Count}.")
            End If

            ' zones (order matters: appended at the end)
            Dim newZ = pick.Where(Function(f) f.Category = Cat.Zone).ToList()
            If newZ.Count > 0 Then
                Dim zones = t.GetJson("ZONES").AsArray()
                Dim names = t.GetJson("ZONE_NAMES").AsObject()
                For Each f In newZ
                    If Not zones.Any(Function(z) z.GetValue(Of String)() = f.Id) Then zones.Add(f.Id)
                    names(f.Id) = NonEmpty(f.Name, f.Id)
                Next
                t.SetJson("ZONES", zones)
                t.SetJson("ZONE_NAMES", names)
                log.Add($"Zones: added {String.Join(", ", newZ.Select(Function(z) z.Name))}.")
            End If

            AddToObject(t, "PETS", Nothing, pick, Cat.Pet, Function(f) JsonValue.Create(NonEmpty(f.Name, f.Id)), log)
            AddToObject(t, "TABARDS", Nothing, pick, Cat.Tabard, Function(f) JsonValue.Create(NonEmpty(f.Name, f.Id)), log)
            AddToObject(t, "PERKMAX", Nothing, pick, Cat.Perk, Function(f) JsonValue.Create(ToInt(f.Extra, 1)), log)
            AddToObject(t, "CODEX", "J", pick, Cat.Jewelry, Function(f) JsonValue.Create(NonEmpty(f.Name, f.Id)), log)
            AddToObject(t, "CODEX", "A", pick, Cat.Armory, Function(f) New JsonArray(JsonValue.Create(NonEmpty(f.Name, f.Id)), JsonValue.Create(ToInt(f.Extra, 3))), log)

            ' perk display names live in IC.p as [icon, name]
            Dim newPerks = pick.Where(Function(f) f.Category = Cat.Perk).ToList()
            If newPerks.Count > 0 Then
                Dim ic = TryCast(t.GetJson("IC"), JsonObject)
                Dim p = TryCast(ic?("p"), JsonObject)
                If p IsNot Nothing Then
                    For Each f In newPerks
                        If Not p.ContainsKey(f.Id) Then p(f.Id) = New JsonArray(JsonValue.Create(NonEmpty(f.Icon, "INV_Misc_QuestionMark")), JsonValue.Create(NonEmpty(f.Name, f.Id)))
                    Next
                    t.SetJson("IC", ic)
                End If
            End If

            If key IsNot Nothing AndAlso key.Changed AndAlso key.Seal IsNot Nothing AndAlso
               findings.Any(Function(f) f.Category = Cat.Key AndAlso f.Include) Then
                t.SetSeal(key.Seal)
                log.Add("Seal key updated to " & key.Seal.Prefix & key.Seal.SecretHex & ".")
            End If

            t.StampVersion()
            log.Add("Version label set to: " & t.VersionLabel())
            Return log
        End Function

        Private Sub AddToObject(t As EditorTemplate, constName As String, subKey As String, pick As List(Of Finding), category As String,
                                make As Func(Of Finding, JsonNode), log As List(Of String))
            Dim items = pick.Where(Function(f) f.Category = category).ToList()
            If items.Count = 0 Then Return
            Dim root = t.GetJson(constName)
            If root Is Nothing Then log.Add($"{category}: the editor has no {constName} table, skipped.") : Return
            Dim obj = If(subKey Is Nothing, root.AsObject(), root(subKey)?.AsObject())
            If obj Is Nothing Then Return
            For Each f In items
                If Not obj.ContainsKey(f.Id) Then obj(f.Id) = make(f)
            Next
            t.SetJson(constName, root)
            log.Add($"{category}: added {items.Count}.")
        End Sub

        Private Function NonEmpty(s As String, fallback As String) As String
            Return If(String.IsNullOrWhiteSpace(s), fallback, s.Trim())
        End Function

        Private Function ToInt(s As String, d As Integer) As Integer
            Dim v As Integer
            Return If(Integer.TryParse(s, v), v, d)
        End Function
    End Module

End Namespace
