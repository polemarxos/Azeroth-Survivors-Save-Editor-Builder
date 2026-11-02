Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Achievements a compiled game does not keep as plain id strings:
    '''  - families made from a format string ("zone9:" + zone, "class:" + class) - expanded for every zone/class;
    '''  - ids the code writes piece by piece ("codex:50") - read by the store emulator;
    '''  - any achievement title + description in the achievement texts that the editor doesn't have, with its id
    '''    taken from the code next to where that title is used.
    ''' </summary>
    Partial Public Class ExeScanner

        Private ReadOnly Generated As New HashSet(Of String)

        ''' <summary>Which family a template prefix belongs to: "zone", "class" or Nothing.</summary>
        Private Function FamilyOf(prefix As String, known As KnownData) As String
            Dim sibs = known.IdsOf(Cat.Achievement).Where(Function(x) x.StartsWith(prefix)).Select(Function(x) x.Substring(prefix.Length)).ToList()
            If sibs.Count = 0 Then Return Nothing
            Dim zones = known.IdsOf(Cat.Zone)
            Dim classes = KnownData.ClassNames.Values.Select(Function(n) n.ToLowerInvariant().Replace(" ", "")).ToHashSet()
            If sibs.All(Function(x) zones.Contains(x)) Then Return "zone"
            If sibs.All(Function(x) classes.Contains(x)) Then Return "class"
            Return Nothing
        End Function

        ''' <summary>Marks every id a format-string family produces for the zones/classes the editor knows as present.</summary>
        Private Sub PrepareGenerated(known As KnownData)
            If Not PeMode Then Return
            For Each p In TemplatePrefixes
                Select Case FamilyOf(p, known)
                    Case "zone"
                        For Each z In known.IdsOf(Cat.Zone) : Generated.Add(p & z) : Next
                    Case "class"
                        For Each c In known.ClsOrder
                            Dim n As String = Nothing
                            If KnownData.ClassNames.TryGetValue(c, n) Then Generated.Add(p & n.ToLowerInvariant().Replace(" ", ""))
                        Next
                End Select
            Next
            ' numbered families (codex:10/50/100, talents:1/9...): when the game has one of them, it builds the others the same way
            For Each g In known.IdsOf(Cat.Achievement).Where(Function(x) Text.RegularExpressions.Regex.IsMatch(x, "^[a-z_]+:\d+[a-z]*$")).GroupBy(Function(x) x.Substring(0, x.IndexOf(":"c) + 1))
                If g.Any(Function(x) Words.ContainsKey(x) OrElse InlineSet.Contains(x)) OrElse TemplatePrefixes.Contains(g.Key) Then
                    For Each x In g : Generated.Add(x) : Next
                End If
            Next
            If TemplatePrefixes.Count > 0 Then Log("Achievement families made by the game: " & String.Join(", ", TemplatePrefixes.Select(Function(x) x & "…")) &
                                                   If(InlineIds.Count > 0, $"; {InlineIds.Count} ids written by code", ""))
        End Sub

        ''' <summary>New zones get the achievements of every zone family ("zone:x", "zone9:x"...), modelled on an existing zone.</summary>
        Private Function TemplateAchievements(known As KnownData, sofar As List(Of Finding)) As List(Of Finding)
            Dim out As New List(Of Finding)
            If Not PeMode Then Return out
            ' every zone (the editor's and new ones) should have each zone family's achievement
            Dim allZones = known.ZoneOrder.Select(Function(z) (Id:=z, Name:=known.Names.GetValueOrDefault(z, z))).ToList()
            For Each f In sofar.Where(Function(x) x.Category = Cat.Zone AndAlso x.Status = FindStatus.New AndAlso x.Include)
                allZones.Add((f.Id, f.Name))
            Next
            Dim have = New HashSet(Of String)(known.IdsOf(Cat.Achievement).Concat(sofar.Where(Function(f) f.Category = Cat.Achievement).Select(Function(f) f.Id)))
            For Each p In TemplatePrefixes.Where(Function(x) FamilyOf(x, known) = "zone")
                ' a sibling to copy title/description from
                Dim sib = known.IdsOf(Cat.Achievement).FirstOrDefault(Function(x) x.StartsWith(p))
                If sib Is Nothing Then Continue For
                Dim sz = sib.Substring(p.Length)
                Dim sName = known.Names.GetValueOrDefault(sz, sz)
                For Each z In allZones
                    Dim id = p & z.Id
                    If have.Contains(id) Then Continue For
                    Dim t = known.GetProp(Cat.Achievement, sib, "name"), d = known.GetProp(Cat.Achievement, sib, "desc")
                    out.Add(New Finding With {.Category = Cat.Achievement, .Id = id,
                        .Name = If(t, "").Replace(sName, z.Name), .Description = If(d, "").Replace(sName, z.Name),
                        .Icon = "INV_Misc_QuestionMark", .Extra = known.AchInfo(sib).Tab,
                        .Notes = $"made by the game for every zone (like {sib}); pick an icon"})
                    Generated.Add(id)
                Next
            Next
            ' class families ("class:" + class): one per class
            For Each p In TemplatePrefixes.Where(Function(x) FamilyOf(x, known) = "class")
                Dim sib = known.IdsOf(Cat.Achievement).FirstOrDefault(Function(x) x.StartsWith(p))
                If sib Is Nothing Then Continue For
                Dim sKey = sib.Substring(p.Length)
                Dim sName = KnownData.ClassNames.Values.FirstOrDefault(Function(n) n.ToLowerInvariant().Replace(" ", "") = sKey)
                If sName Is Nothing Then Continue For
                For Each c In known.ClsOrder
                    Dim cn As String = Nothing
                    If Not KnownData.ClassNames.TryGetValue(c, cn) Then Continue For
                    Dim id = p & cn.ToLowerInvariant().Replace(" ", "")
                    If have.Contains(id) Then Continue For
                    Dim t = known.GetProp(Cat.Achievement, sib, "name"), d = known.GetProp(Cat.Achievement, sib, "desc")
                    Dim sIcon = known.GetProp(Cat.Achievement, sib, "icon")
                    out.Add(New Finding With {.Category = Cat.Achievement, .Id = id,
                        .Name = If(t, "").Replace(sName, cn), .Description = If(d, "").Replace(sName, cn),
                        .Icon = If(sIcon Is Nothing OrElse sIcon = sib, id, "INV_Misc_QuestionMark"), .Extra = known.AchInfo(sib).Tab,
                        .Notes = $"made by the game for every class (like {sib})"})
                Next
            Next
            If out.Count > 0 Then Log($"Achievements: {out.Count} zone/class achievement(s) the editor is missing.")
            Return out
        End Function

        ''' <summary>
        ''' Every achievement title + description in the game's achievement texts that the editor doesn't have.
        ''' The id comes from the code that uses the title (an id string or an id written by code next to it).
        ''' </summary>
        Private Function TitleAchievements(known As KnownData, sofar As List(Of Finding)) As List(Of Finding)
            Dim out As New List(Of Finding)
            If Not PeMode Then Return out
            Dim ids = known.IdsOf(Cat.Achievement)
            Dim titles As New HashSet(Of String)(ids.Select(Function(i) known.GetProp(Cat.Achievement, i, "name")).Where(Function(x) x IsNot Nothing))
            Dim descs As New HashSet(Of String)(ids.Select(Function(i) known.GetProp(Cat.Achievement, i, "desc")).Where(Function(x) x IsNot Nothing))
            For Each f In sofar.Where(Function(x) x.Category = Cat.Achievement)
                titles.Add(f.Name) : descs.Add(f.Description)
            Next
            Dim otherNames As New HashSet(Of String)(known.Names.Values)

            ' the stretches of text where the known titles are
            Dim pos As New List(Of Integer)
            For i = 0 To Fields.Count - 1
                If titles.Contains(Fields(i).Text) Then pos.Add(i)
            Next
            If pos.Count < 3 Then Return out
            Dim spans As New List(Of (A As Integer, B As Integer))
            Dim st = pos(0), prev = pos(0), n = 1
            For Each p In pos.Skip(1)
                If Fields(p).Offset - Fields(prev).Offset > 3000 Then
                    If n >= 3 Then spans.Add((st, prev))
                    st = p : n = 0
                End If
                prev = p : n += 1
            Next
            If n >= 3 Then spans.Add((st, prev))

            Dim knownPrefixes = New HashSet(Of String)(ids.Where(Function(x) x.Contains(":"c)).Select(Function(x) x.Substring(0, x.IndexOf(":"c) + 1)))
            Dim usedIds As New HashSet(Of String)(ids)
            For Each f In sofar : usedIds.Add(f.Id) : Next

            For Each sp In spans
                For i = sp.A To sp.B
                    Dim t = Fields(i).Text
                    If titles.Contains(t) OrElse Not IsTitle(t) OrElse t.Contains("{"c) OrElse otherNames.Contains(t) Then Continue For
                    Dim di = -1
                    For j = i + 1 To Math.Min(i + 2, Fields.Count - 1)
                        If IsSentence(Fields(j).Text) Then di = j : Exit For
                    Next
                    If di < 0 OrElse descs.Contains(Fields(di).Text) OrElse descs.Contains(FillPlaceholders(Fields(di).Text)) Then Continue For
                    Dim desc = FillPlaceholders(Fields(di).Text)
                    ' icon: right after the description (or after the id that follows it)
                    Dim icon As String = Nothing, idTxt As String = Nothing
                    For j = di + 1 To Math.Min(di + 3, Fields.Count - 1)
                        Dim x = Fields(j).Text
                        If icon Is Nothing AndAlso IsIcon(x) Then icon = x
                        If idTxt Is Nothing AndAlso IdRx.IsMatch(x) AndAlso Not usedIds.Contains(x) AndAlso x.Contains(":"c) Then idTxt = x
                    Next
                    If idTxt Is Nothing Then idTxt = IdNearCode(Fields(i).Offset, usedIds, knownPrefixes)
                    If icon Is Nothing Then icon = IconNearCode(Fields(i).Offset)
                    Dim f As New Finding With {.Category = Cat.Achievement, .Name = t, .Description = desc, .Icon = If(icon, "INV_Misc_QuestionMark"),
                        .Offset = -1, .Extra = TabFor(idTxt, known)}
                    If idTxt IsNot Nothing Then
                        f.Id = idTxt : usedIds.Add(idTxt)
                        f.Notes = "found by its title; id read from the game code"
                    Else
                        f.Id = "?" & Slug(t) : f.Include = False
                        f.Notes = "found by its title, but its id is built while the game runs - type the save id in the Id column to add it"
                    End If
                    out.Add(f)
                    titles.Add(t) : descs.Add(desc)
                Next
            Next
            If out.Count > 0 Then Log($"Achievements: {out.Count} more found by title.")
            Return out
        End Function

        Private Shared Function Slug(t As String) As String
            Return New String(t.ToLowerInvariant().Where(Function(c) Char.IsLetterOrDigit(c)).ToArray())
        End Function

        Private Shared Function TabFor(id As String, known As KnownData) As String
            If id Is Nothing Then Return "Feats"
            Dim pre = If(id.Contains(":"c), id.Substring(0, id.IndexOf(":"c)), id)
            Dim g = known.AchInfo.Where(Function(p) p.Key.StartsWith(pre & ":") OrElse p.Key = pre).GroupBy(Function(p) p.Value.Tab).OrderByDescending(Function(x) x.Count()).FirstOrDefault()
            Return If(g Is Nothing, "Feats", g.Key)
        End Function

        ''' <summary>Code offsets that load the string at stringOff.</summary>
        Private Iterator Function CodeUses(stringOff As Long) As IEnumerable(Of Integer)
            For Each c In CodeStr
                If c.Str = stringOff Then Yield c.Code
            Next
        End Function

        ''' <summary>
        ''' The id the code uses right before it uses the title (the game sets the id first, then title, text and icon).
        ''' Ids written by code and id strings both count; ids just after the title are the fallback.
        ''' </summary>
        Private Function IdNearCode(titleOff As Long, used As HashSet(Of String), prefixes As HashSet(Of String)) As String
            Dim best As String = Nothing, bd = Integer.MaxValue
            Dim consider = Sub(id As String, idCode As Integer, code As Integer)
                               If used.Contains(id) OrElse Not IdRx.IsMatch(id) Then Return
                               Dim before = idCode <= code
                               Dim d = Math.Abs(code - idCode)
                               Dim looksAch = prefixes.Any(Function(p) id.StartsWith(p))
                               If Not looksAch AndAlso (Not before OrElse d > &H80) Then Return   ' a bare id must sit right before the title
                               If d > &H200 Then Return
                               Dim score = If(before, d, d + &H1000)                            ' prefer ids set before the title
                               If score < bd Then best = id : bd = score
                           End Sub
            For Each code In CodeUses(titleOff)
                For Each x In InlineIds
                    consider(x.Id, x.Code, code)
                Next
                Dim i = CodeStr.BinarySearch((code, 0), Comparer(Of (Code As Integer, Str As Integer)).Create(Function(a, c) a.Code.CompareTo(c.Code)))
                If i < 0 Then i = Not i
                For k = Math.Max(0, i - 40) To Math.Min(CodeStr.Count - 1, i + 40)
                    Dim c = CodeStr(k)
                    consider(Encoding.UTF8.GetString(Data, c.Str, RefLen(c.Str)), c.Code, code)
                Next
            Next
            Return best
        End Function

        Private Function IconNearCode(titleOff As Long) As String
            Dim best As String = Nothing, bd = Integer.MaxValue
            For Each code In CodeUses(titleOff)
                For Each c In CodeStr
                    Dim d = Math.Abs(c.Code - code)
                    If d >= &H400 OrElse d >= bd Then Continue For
                    Dim txt = Encoding.UTF8.GetString(Data, c.Str, RefLen(c.Str))
                    If IsIcon(txt) Then best = txt : bd = d
                Next
            Next
            Return best
        End Function
    End Class

End Namespace
