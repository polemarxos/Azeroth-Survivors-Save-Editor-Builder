Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Codex legendary abilities. Each class has a list of ability ids in the game (an array of string records);
    ''' the codex in the save uses "legendary:&lt;id&gt;". The list is found from the ids the editor already knows,
    ''' read in full, and compared: new abilities are added in the game's order, dropped ones are offered for removal.
    ''' Name and icon come from the ability's own record (id, name, icon).
    ''' </summary>
    Partial Public Class ExeScanner

        Private Function LegendaryLists(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            If Not PeMode OrElse known.Legendary.Count = 0 Then Return out
            Dim classOf As New Dictionary(Of String, Integer)
            For Each kv In known.Legendary
                For Each id In kv.Value : classOf.TryAdd(id, kv.Key) : Next
            Next
            ' the ability records (id, name, icon, ...) the codex is built from, in the game's order
            Dim recs = AbilityRecords(classOf.Keys)
            If recs.Count < 10 Then
                Log("Codex legendary: ability records not found.")
                Return out
            End If
            ' records of one class sit together: a new id belongs to the class of the record before it
            Dim cur = 0
            Dim gameByClass As New Dictionary(Of Integer, List(Of (Id As String, Name As String, Icon As String)))
            Dim firstKnown = recs.FindIndex(Function(r) classOf.ContainsKey(r.Id))
            If firstKnown >= 0 Then cur = classOf(recs(firstKnown).Id)
            ' records after the last class ability (racials and other shared abilities) belong to no class
            Dim lastKnown = recs.FindLastIndex(Function(r) classOf.ContainsKey(r.Id))
            For i = 0 To lastKnown
                Dim r = recs(i)
                Dim c As Integer
                If classOf.TryGetValue(r.Id, c) Then cur = c
                If Not gameByClass.ContainsKey(cur) Then gameByClass(cur) = New List(Of (Id As String, Name As String, Icon As String))
                gameByClass(cur).Add(r)
            Next
            Dim inGame As New HashSet(Of String)(recs.Select(Function(r) r.Id))
            If lastKnown < recs.Count - 1 Then Log($"Codex legendary: {recs.Count - 1 - lastKnown} shared abilities after the class lists are not added (racials etc.).")
            For Each kv In known.Legendary
                Dim c = kv.Key, lst = kv.Value
                Dim cname = KnownData.ClassNames.GetValueOrDefault(c, "Class " & c)
                Dim game = gameByClass.GetValueOrDefault(c, New List(Of (Id As String, Name As String, Icon As String)))
                Dim added = game.Where(Function(r) Not lst.Contains(r.Id)).ToList()
                ' the editor's order stays; new abilities go after, in the game's order
                Dim order = String.Join(",", lst.Concat(added.Select(Function(r) r.Id)))
                For Each r In added
                    out.Add(New Finding With {.Category = Cat.Legendary, .Id = r.Id, .Name = If(r.Name, Pretty(r.Id)), .Icon = If(r.Icon, "INV_Misc_QuestionMark"),
                        .Extra = cname, .Path = "L/" & c, .Data = New Dictionary(Of String, String) From {{"order", order}},
                        .Notes = $"new {cname} legendary ability (codex entry legendary:{r.Id})"})
                Next
                For Each id In lst.Where(Function(x) Not inGame.Contains(x))
                    out.Add(New Finding With {.Category = Cat.Legendary, .Status = FindStatus.Changed, .Prop = "removed", .Id = id, .Include = False,
                        .Name = known.GetProp(Cat.Ability, id, "name"), .Extra = cname, .Path = "L/" & c,
                        .Data = New Dictionary(Of String, String) From {{"order", order}},
                        .Notes = $"no longer one of the game's {cname} abilities; older saves can still have it - tick to remove it from the editor's codex"})
                Next
            Next
            Log($"Codex legendary: {recs.Count} ability records; {out.Where(Function(f) f.Status = FindStatus.New).Count()} new, {out.Where(Function(f) f.Status = FindStatus.Changed).Count()} no longer in the game.")
            Return out
        End Function

        ''' <summary>
        ''' The table of ability records: each record starts with id, name and icon strings; records follow each other
        ''' at a fixed size. Found from known ids, then read from end to end.
        ''' </summary>
        Private Function AbilityRecords(knownIds As IEnumerable(Of String)) As List(Of (Id As String, Name As String, Icon As String))
            Dim res As New List(Of (Id As String, Name As String, Icon As String))
            Dim isRec = Function(p As Integer) As Boolean
                            Dim id = StrAt(p)
                            If id Is Nothing OrElse Not IdRx.IsMatch(id) Then Return False
                            Dim nm = StrAt(p + 16), ic = StrAt(p + 32)
                            Return nm IsNot Nothing AndAlso ic IsNot Nothing AndAlso IsTitle(nm) AndAlso IsIcon(ic)
                        End Function
            Dim pos As New List(Of Integer)
            For Each id In knownIds
                For Each fi In FieldsWithText(id)
                    Dim pl As List(Of Integer) = Nothing
                    If Not PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then Continue For
                    For Each p In pl
                        If isRec(p) Then pos.Add(p)
                    Next
                Next
            Next
            If pos.Count < 10 Then Return res
            pos = pos.Distinct().OrderBy(Function(x) x).ToList()
            Dim diffs As New List(Of Integer)
            For i = 1 To pos.Count - 1
                Dim df = pos(i) - pos(i - 1)
                If df > 16 AndAlso df <= 512 Then diffs.Add(df)
            Next
            If diffs.Count = 0 Then Return res
            Dim stride = diffs.GroupBy(Function(x) x).OrderByDescending(Function(g) g.Count()).First().Key
            ' the longest run of records at that size
            Dim bestStart = pos(0), bestLen = 0
            For Each st In pos
                If st - stride >= 0 AndAlso isRec(st - stride) Then Continue For   ' not the start of a run
                Dim n = 0, p = st
                While isRec(p) : n += 1 : p += stride : End While
                If n > bestLen Then bestLen = n : bestStart = st
            Next
            Dim q = bestStart
            For k = 0 To bestLen - 1
                res.Add((StrAt(q), StrAt(q + 16), StrAt(q + 32)))
                q += stride
            Next
            Return res
        End Function

        ''' <summary>Start of an array of 16-byte string records holding (most of) the given ids in order.</summary>
        Private Function FindLooseArray(items As List(Of String)) As Integer
            Dim best = -1, bestScore = 0
            For i = 0 To items.Count - 1
                For Each fi In FieldsWithText(items(i))
                    Dim pl As List(Of Integer) = Nothing
                    If Not PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then Continue For
                    For Each pp In pl
                        Dim st = pp - 16 * i
                        If st < 0 Then Continue For
                        Dim score = 0
                        For k = 0 To items.Count - 1
                            If StrAt(st + 16 * k) = items(k) Then score += 1
                        Next
                        ' an id can move by one place when another is inserted before it: allow a shifted match too
                        Dim score2 = 0
                        For k = 0 To items.Count - 1
                            Dim sk = StrAt(st + 16 * k)
                            If sk IsNot Nothing AndAlso items.Contains(sk) Then score2 += 1
                        Next
                        Dim sc = Math.Max(score, score2)
                        If sc > bestScore Then bestScore = sc : best = st
                    Next
                Next
            Next
            Return If(bestScore >= Math.Max(3, CInt(items.Count * 0.6)), best, -1)
        End Function

        ''' <summary>The ability record (id, name, icon as consecutive strings) for an ability id.</summary>
        Private Function AbilityRecord(id As String) As (Name As String, Icon As String)
            For Each fi In FieldsWithText(id)
                Dim pl As List(Of Integer) = Nothing
                If Not PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then Continue For
                For Each p In pl
                    Dim nm = StrAt(p + 16), ic = StrAt(p + 32)
                    If nm IsNot Nothing AndAlso ic IsNot Nothing AndAlso IsTitle(nm) AndAlso IsIcon(ic) Then Return (nm, ic)
                Next
            Next
            Return (Nothing, Nothing)
        End Function
    End Class

End Namespace
