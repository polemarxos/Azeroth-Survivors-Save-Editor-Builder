Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Save file pass. When a save made by this game version is given, every id written in it is an id the game
    ''' really uses: codex entries ("seen"), zones ("beat", "hbeat", "open", "pick"), achievements, pets, tabards.
    ''' Ids the editor doesn't know become new rows, named from the game file; ids the save has confirm rows found
    ''' by the other passes.
    ''' </summary>
    Partial Public Class ExeScanner

        ''' <summary>The save's text (filled in by FindKey when the save opens with the key).</summary>
        Public Property SavePlain As String

        Private Class SaveIds
            Public Seen As New List(Of String), Zones As New List(Of String), Ach As New List(Of String)
            Public Pets As New List(Of String), Tabards As New List(Of String)
        End Class

        Private Shared Function ParseSave(text As String) As SaveIds
            Dim s As New SaveIds()
            Dim addZone = Sub(z As String)
                              z = z.Trim()
                              If z <> "" AndAlso IdRx.IsMatch(z) AndAlso Not s.Zones.Contains(z) Then s.Zones.Add(z)
                          End Sub
            For Each line In text.Replace(vbCr, "").Split(vbLf(0))
                Dim p = line.Split("|"c)
                If p.Length < 2 Then Continue For
                Select Case p(0)
                    Case "seen"
                        For Each x In p(1).Split(","c, StringSplitOptions.RemoveEmptyEntries)
                            Dim k = x.LastIndexOf("="c)
                            s.Seen.Add(If(k > 0, x.Substring(0, k), x))
                        Next
                    Case "beat"
                        If p.Length > 2 Then
                            For Each z In p(2).Split(","c) : addZone(z) : Next
                        End If
                    Case "hbeat"
                        If p.Length > 2 Then
                            For Each z In p(2).Split(","c) : addZone(z.Split(":"c)(0)) : Next
                        End If
                    Case "open"
                        If p.Length > 2 Then addZone(p(2))
                    Case "pick"
                        If p.Length > 4 Then addZone(p(4))
                    Case "ach"
                        s.Ach.AddRange(p(1).Split(","c, StringSplitOptions.RemoveEmptyEntries))
                    Case "pets"
                        s.Pets.AddRange(p(1).Split(","c, StringSplitOptions.RemoveEmptyEntries))
                    Case "tabard"
                        s.Tabards.AddRange(p(1).Split(","c, StringSplitOptions.RemoveEmptyEntries))
                End Select
            Next
            Return s
        End Function

        Private Function SavePass(known As KnownData, res As List(Of Finding)) As List(Of Finding)
            Dim out As New List(Of Finding)
            If String.IsNullOrEmpty(SavePlain) Then Return out
            Dim s = ParseSave(SavePlain)
            Dim found As New HashSet(Of String)(res.Where(Function(f) f.Status = FindStatus.New).Select(Function(f) f.Category & ">" & f.Id))
            Dim isFound = Function(category As String, id As String) found.Contains(category & ">" & id)

            ' --- codex: rings & trinkets, armory, legendary abilities ---
            Dim legendaryKnown As New HashSet(Of String)(known.Legendary.Values.SelectMany(Function(x) x))
            Dim seenSet As New HashSet(Of String)(s.Seen)
            For Each f In res.Where(Function(x) x.Category = Cat.Legendary)
                ' the save shows what the game's codex really offers
                If f.Status = FindStatus.New Then f.Include = seenSet.Contains("legendary:" & f.Id) OrElse f.Include AndAlso Not s.Seen.Any(Function(x) x.StartsWith("legendary:"))
                If f.Status = FindStatus.Changed AndAlso seenSet.Contains("legendary:" & f.Id) Then f.Include = False : f.Notes &= " (your save has it)"
                If f.Status = FindStatus.New AndAlso Not f.Include Then f.Notes &= " - not in your save's codex, tick to add anyway"
            Next
            For Each id In s.Seen
                If id.StartsWith("legendary:") Then
                    Dim a = id.Substring(10)
                    If legendaryKnown.Contains(a) OrElse isFound(Cat.Legendary, a) Then Continue For
                    Dim rec = AbilityRecord(a)
                    out.Add(New Finding With {.Category = Cat.Legendary, .Id = a, .Name = If(rec.Name, Pretty(a)), .Icon = If(rec.Icon, "INV_Misc_QuestionMark"),
                        .Include = False, .Notes = "in your save's codex; its class is unknown - add it by hand"})
                    Continue For
                End If
                If known.IdsOf(Cat.Jewelry).Contains(id) OrElse known.IdsOf(Cat.Armory).Contains(id) Then Continue For
                If isFound(Cat.Jewelry, id) OrElse isFound(Cat.Armory, id) Then Continue For
                Dim category = CodexKind(id, known)
                Dim nm = NeighbourAfter(id, 1), ic = NeighbourAfter(id, 2)
                Dim f As New Finding With {.Category = category, .Id = id, .Name = If(nm IsNot Nothing AndAlso IsTitle(nm), nm, Pretty(id)),
                    .Icon = If(ic IsNot Nothing AndAlso IsIcon(ic), ic, "INV_Misc_QuestionMark"),
                    .Notes = "in your save's codex"}
                If category = Cat.Armory Then f.Extra = "3"
                out.Add(f)
                found.Add(category & ">" & id)
            Next

            ' --- zones (the ids the save uses: beat|class|zone,zone...) ---
            For Each z In s.Zones
                If known.IdsOf(Cat.Zone).Contains(z) OrElse isFound(Cat.Zone, z) Then Continue For
                Dim nm = ZoneName(z)
                Dim prev = PreviousZone(z, s.Zones, known)
                out.Add(New Finding With {.Category = Cat.Zone, .Id = z, .Name = If(nm, Pretty(z)), .Path = If(prev Is Nothing, "", "after:" & prev),
                    .Notes = "zone id from your save" & If(prev Is Nothing, "", " - goes after " & known.Names.GetValueOrDefault(prev, prev) & " as in your save")})
                found.Add(Cat.Zone & ">" & z)
            Next

            ' --- achievements, pets, tabards the editor lacks ---
            For Each a In s.Ach
                If known.IdsOf(Cat.Achievement).Contains(a) OrElse isFound(Cat.Achievement, a) Then Continue For
                out.Add(New Finding With {.Category = Cat.Achievement, .Id = a, .Name = Pretty(a), .Icon = If(IconAfterId(a), "INV_Misc_QuestionMark"),
                    .Extra = TabFor(a, known), .Include = False, .Notes = "in your save but not found in the game file - check the name before adding"})
            Next
            For Each p In s.Pets
                If known.IdsOf(Cat.Pet).Contains(p) OrElse isFound(Cat.Pet, p) Then Continue For
                Dim nm = NeighbourAfter(p, 1)
                out.Add(New Finding With {.Category = Cat.Pet, .Id = p, .Name = If(nm IsNot Nothing AndAlso IsTitle(nm), nm, Pretty(p)), .Notes = "pet id from your save"})
            Next
            For Each t In s.Tabards
                If known.IdsOf(Cat.Tabard).Contains(t) OrElse isFound(Cat.Tabard, t) Then Continue For
                out.Add(New Finding With {.Category = Cat.Tabard, .Id = t, .Name = "Tabard " & t, .Include = False, .Notes = "tabard id from your save - type its name"})
            Next
            Log($"Save file: {s.Seen.Count} codex entries, {s.Zones.Count} zones, {s.Ach.Count} achievements; {out.Count} id(s) the editor didn't know.")
            Return out
        End Function

        ''' <summary>The string k places after an id in the game file (name at +1, icon at +2 for codex entries).</summary>
        Private Function NeighbourAfter(id As String, k As Integer) As String
            For Each fi In FieldsWithText(id)
                If fi + k < Fields.Count Then Return Fields(fi + k).Text
            Next
            Return Nothing
        End Function

        ''' <summary>Rings/trinkets or armory: whichever known table the id sits closer to.</summary>
        Private Function CodexKind(id As String, known As KnownData) As String
            Dim offs = FieldsWithText(id).Select(Function(i) Fields(i).Offset).ToList()
            If offs.Count = 0 Then Return Cat.Jewelry
            Dim dist = Function(ids As HashSet(Of String)) As Long
                           Dim best = Long.MaxValue
                           For Each k In ids
                               For Each fi In FieldsWithText(k)
                                   For Each o In offs
                                       best = Math.Min(best, Math.Abs(Fields(fi).Offset - o))
                                   Next
                               Next
                           Next
                           Return best
                       End Function
            Return If(dist(known.IdsOf(Cat.Armory)) < dist(known.IdsOf(Cat.Jewelry)), Cat.Armory, Cat.Jewelry)
        End Function

        ''' <summary>A zone's display name from its zone record (id, name, subtitle...).</summary>
        Private Function ZoneName(z As String) As String
            For Each fi In FieldsWithText(z)
                Dim pl As List(Of Integer) = Nothing
                If PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then
                    For Each p In pl
                        Dim nm = StrAt(p + 16)
                        If nm IsNot Nothing AndAlso IsTitle(nm) Then Return nm
                    Next
                End If
                If fi + 1 < Fields.Count AndAlso IsTitle(Fields(fi + 1).Text) Then Return Fields(fi + 1).Text
            Next
            Return Nothing
        End Function

        ''' <summary>The known zone listed just before z in the save (zones are written in unlock order).</summary>
        Private Shared Function PreviousZone(z As String, saveZones As List(Of String), known As KnownData) As String
            Dim i = saveZones.IndexOf(z)
            For k = i - 1 To 0 Step -1
                If known.IdsOf(Cat.Zone).Contains(saveZones(k)) Then Return saveZones(k)
            Next
            Return Nothing
        End Function
    End Class

End Namespace
