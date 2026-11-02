Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Core

    ''' <summary>
    ''' Second pass: reads the values stored with each table entry (icon, name, max rank, talent row/column...).
    ''' The builder learns where each value sits from entries the editor already knows - either by its key
    ''' (e.g. icon:"...") or by its position after the id - and then reads the same place for new entries.
    ''' Values that changed in the game for entries the editor already has are reported as "Changed".
    ''' </summary>
    Partial Public Class ExeScanner

        Private Structure Tok
            Public Key As String
            Public Value As String
        End Structure

        Private Shared ReadOnly TokRx As New Regex(
            "(?:[""']?([A-Za-z_$][\w$]*)[""']?\s*:\s*)?(""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|(?<![\w$.])-?\d+(?:\.\d+)?(?![\w.])|\btrue\b|\bfalse\b)",
            RegexOptions.Compiled)

        Private Class Rule
            Public Key As String
            Public Index As Integer = -1
            Public IconScan As Boolean
            Public Hits As Integer, Total As Integer
            Public ReadOnly Property Accuracy As Double
                Get
                    Return If(Total = 0, 0, Hits / Total)
                End Get
            End Property
            ''' <summary>Good enough to report changes to values the editor already has.</summary>
            Public ReadOnly Property Strong As Boolean
                Get
                    Return Not IconScan AndAlso Hits >= 3 AndAlso Accuracy >= 0.85
                End Get
            End Property
            Public Function Apply(toks As List(Of Tok)) As String
                If Key IsNot Nothing Then
                    For Each t In toks
                        If t.Key = Key Then Return t.Value
                    Next
                    Return Nothing
                End If
                If Index >= 0 Then Return If(Index < toks.Count, toks(Index).Value, Nothing)
                If IconScan Then
                    For Each t In toks
                        If IsIcon(t.Value) Then Return t.Value
                    Next
                End If
                Return Nothing
            End Function
            Public Overrides Function ToString() As String
                Dim how = If(Key IsNot Nothing, "key '" & Key & "'", If(Index >= 0, "position +" & Index, "first icon name"))
                Return $"{how} ({Hits}/{Total})"
            End Function
        End Class

        ''' <summary>The values written after an id, up to where the next entry starts.</summary>
        Private Function EntryTokens(off As Long, id As String, limit As Long, Optional pairNear As List(Of (Lo As Long, Hi As Long)) = Nothing) As List(Of Tok)
            Dim toks As New List(Of Tok)
            If PeMode Then
                ' compiled program: neighbouring strings plus the record (struct) the id belongs to
                AddNeighbourTokens(toks, off, id)
                If pairNear IsNot Nothing Then AddStructTokens(toks, PairOf(off, pairNear))
                Return toks
            End If
            ' start right after the id (and its closing quote), so the id itself is never read as a value
            Dim st = off + id.Length
            If st < Data.LongLength AndAlso (Data(CInt(st)) = 34 OrElse Data(CInt(st)) = 39) Then st += 1
            Dim en = Math.Min(Math.Min(limit, off + 700), Data.LongLength)
            If en <= st Then Return toks
            Dim text = Encoding.Latin1.GetString(Data, CInt(st), CInt(en - st))
            ' source code has quoted values; an apostrophe inside a plain name (Zul'Gurub) does not count
            Dim isCode = text.IndexOf(""""c) >= 0 OrElse text.Contains(":'") OrElse text.Contains("',") OrElse text.Contains("['")
            If isCode Then
                For Each m As Match In TokRx.Matches(text)
                    Dim v = m.Groups(2).Value
                    If v.Length >= 2 AndAlso (v(0) = """"c OrElse v(0) = "'"c) Then
                        v = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(v.Substring(1, v.Length - 2))).Replace("\'", "'").Replace("\""", """")
                    End If
                    If v = id Then Continue For
                    toks.Add(New Tok With {.Key = If(m.Groups(1).Success, m.Groups(1).Value, Nothing), .Value = v})
                Next
            Else
                ' plain strings one after another (no source code around them)
                Dim i = FirstFieldAfter(off)
                While i < Fields.Count AndAlso Fields(i).Offset < en
                    If Fields(i).Text <> id Then toks.Add(New Tok With {.Value = Fields(i).Text})
                    i += 1
                End While
            End If
            Return toks
        End Function

        Private Function LearnRule(samples As List(Of (Toks As List(Of Tok), Known As String)), allowIconScan As Boolean) As Rule
            Dim withKnown = samples.Where(Function(x) x.Known IsNot Nothing).ToList()
            Dim keyHits As New Dictionary(Of String, Integer)
            Dim idxHits As New Dictionary(Of Integer, Integer)
            For Each smp In withKnown
                Dim keys As New HashSet(Of String)
                For i = 0 To smp.Toks.Count - 1
                    If smp.Toks(i).Value <> smp.Known Then Continue For
                    If smp.Toks(i).Key IsNot Nothing Then keys.Add(smp.Toks(i).Key)
                    If Not PeMode Then idxHits(i) = idxHits.GetValueOrDefault(i) + 1
                Next
                For Each k In keys : keyHits(k) = keyHits.GetValueOrDefault(k) + 1 : Next
            Next
            Dim best As Rule = Nothing
            If keyHits.Count > 0 Then
                Dim kb = keyHits.OrderByDescending(Function(x) x.Value).First()
                best = New Rule With {.Key = kb.Key, .Hits = kb.Value, .Total = withKnown.Count}
            End If
            If idxHits.Count > 0 Then
                Dim ib = idxHits.OrderByDescending(Function(x) x.Value).First()
                If best Is Nothing OrElse ib.Value > best.Hits Then best = New Rule With {.Index = ib.Key, .Hits = ib.Value, .Total = withKnown.Count}
            End If
            If best IsNot Nothing AndAlso best.Hits >= 3 AndAlso best.Accuracy >= 0.7 Then Return best
            If allowIconScan AndAlso samples.Count >= 3 Then
                Dim n = samples.Where(Function(x) x.Toks.Any(Function(t) IsIcon(t.Value))).Count()
                If n >= samples.Count * 0.7 Then Return New Rule With {.IconScan = True, .Hits = n, .Total = samples.Count}
            End If
            Return Nothing
        End Function

        ''' <summary>First occurrence of each id inside the table that has the table's layout.</summary>
        Private Function EntryOffsets(ids As IEnumerable(Of String), tb As (R As Region, Sh As Shape), known As KnownData) As Dictionary(Of String, Long)
            Dim d As New Dictionary(Of String, Long)
            For Each id In ids
                If known.Names.ContainsKey(id) AndAlso tb.Sh.Pre Is Nothing AndAlso tb.Sh.Post Is Nothing Then
                    Dim bo = BestOcc(id, tb.R, known)
                    If bo >= 0 Then d(id) = bo
                    Continue For
                End If
                Dim l = Occ(id)
                If l Is Nothing Then Continue For
                For Each o In l
                    If Not o.Alone OrElse Not tb.R.Contains(o.Off) Then Continue For
                    If tb.Sh.Pre IsNot Nothing AndAlso Ctx(o.Off, tb.Sh.Pre.Length, id.Length, True) <> tb.Sh.Pre Then Continue For
                    If tb.Sh.Post IsNot Nothing AndAlso Ctx(o.Off, tb.Sh.Post.Length, id.Length, False) <> tb.Sh.Post Then Continue For
                    d(id) = o.Off
                    Exit For
                Next
            Next
            Return d
        End Function

        ''' <summary>Properties read for each category, and the column they go in.</summary>
        Private Shared Function PropsOf(category As String) As String()
            Select Case category
                Case Cat.Achievement : Return {"icon", "name", "desc"}
                Case Cat.Pet, Cat.Jewelry, Cat.Tabard, Cat.Ability : Return {"icon", "name"}
                Case Cat.Perk : Return {"icon", "name", "max"}
                Case Cat.Armory : Return {"icon", "name", "rarity"}
                Case Cat.Talent : Return {"icon", "name", "max", "row", "col", "tree"}
                Case Else : Return {}
            End Select
        End Function

        Private Function ReadProperties(known As KnownData, sofar As List(Of Finding)) As List(Of Finding)
            Dim out As New List(Of Finding)
            For Each kv In Tables
                Dim category = kv.Key
                Dim props = PropsOf(category)
                If props.Length = 0 Then Continue For
                Dim ids = known.IdsOf(category)
                Dim newOnes = sofar.Where(Function(f) f.Category = category AndAlso f.Status = FindStatus.New).ToList()
                Dim offs = EntryOffsets(ids, kv.Value, known)
                If offs.Count < 3 Then Continue For
                Dim newOffs As New Dictionary(Of Finding, Long)
                For Each f In newOnes
                    If f.Offset >= 0 Then newOffs(f) = f.Offset
                Next
                ' each entry ends where the next one starts
                Dim starts = offs.Values.Concat(newOffs.Values).Distinct().OrderBy(Function(x) x).ToList()
                Dim limitOf = Function(o As Long) As Long
                                  Dim i = starts.BinarySearch(o)
                                  If i < 0 Then i = Not i Else i += 1
                                  Return If(i < starts.Count, starts(i), o + 700)
                              End Function
                Dim pr = If(PeMode, PairRegion(ids), Nothing)
                Dim toksOf As New Dictionary(Of String, List(Of Tok))
                For Each p In offs : toksOf(p.Key) = EntryTokens(p.Value, p.Key, limitOf(p.Value), pr) : Next
                Dim newToks As New Dictionary(Of Finding, List(Of Tok))
                For Each p In newOffs : newToks(p.Key) = EntryTokens(p.Value, p.Key.Id, limitOf(p.Value), pr) : Next

                Dim rules As New Dictionary(Of String, Rule)
                Dim learnt As New List(Of String)
                For Each prop In props
                    Dim pp = prop
                    Dim samples = offs.Keys.Select(Function(id) (toksOf(id), known.GetProp(category, id, pp))).ToList()
                    Dim r = LearnRule(samples, pp = "icon")
                    If r IsNot Nothing Then rules(prop) = r : learnt.Add(prop & " = " & r.ToString())
                Next

                Log($"  {category} values: " & If(learnt.Count = 0, "none recognised", String.Join(", ", learnt)))

                ' fill in new entries
                For Each p In newToks
                    Dim f = p.Key, tk = p.Value
                    Dim v As String
                    v = rules.GetValueOrDefault("icon")?.Apply(tk)
                    If v IsNot Nothing AndAlso IsIcon(v) Then f.Icon = v
                    v = rules.GetValueOrDefault("name")?.Apply(tk)
                    If v IsNot Nothing AndAlso IsTitle(v) Then f.Name = v
                    v = rules.GetValueOrDefault("desc")?.Apply(tk)
                    If v IsNot Nothing AndAlso IsSentence(v) Then f.Description = v
                    If category = Cat.Perk Then
                        v = rules.GetValueOrDefault("max")?.Apply(tk)
                        If IsSmallInt(v) Then f.Extra = v : f.Notes = ""
                    End If
                    If category = Cat.Armory Then
                        v = rules.GetValueOrDefault("rarity")?.Apply(tk)
                        If IsSmallInt(v) Then f.Extra = v
                    End If
                    If category = Cat.Talent Then PlaceNewTalent(known, f, tk, rules, offs)
                    If category <> Cat.Talent AndAlso String.IsNullOrEmpty(f.Icon) Then f.Icon = "INV_Misc_QuestionMark"
                Next

                ' values that changed in the game
                out.AddRange(Changes(known, category, offs.Keys, toksOf, rules))
            Next
            Return out
        End Function

        Private Shared Function IsSmallInt(v As String) As Boolean
            Dim n As Integer
            Return v IsNot Nothing AndAlso Integer.TryParse(v, n) AndAlso n >= 0 AndAlso n < 100
        End Function

        Private Function Changes(known As KnownData, category As String, ids As IEnumerable(Of String),
                                 toksOf As Dictionary(Of String, List(Of Tok)), rules As Dictionary(Of String, Rule)) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim checks = PropsOf(category).Where(Function(p) rules.ContainsKey(p) AndAlso rules(p).Strong).ToList()
            If category = Cat.Talent Then
                ' row/col/max/tree are reported together as one "talent" change
                Dim pos = {"max", "row", "col", "tree"}
                Dim posOk = pos.Take(3).All(Function(p) checks.Contains(p))
                checks = checks.Where(Function(p) Not pos.Contains(p)).ToList()
                If posOk Then
                    For Each id In ids
                        Dim ti As TalentInfo = Nothing
                        If Not known.Talents.TryGetValue(id, ti) Then Continue For
                        Dim tk = toksOf(id)
                        Dim mx = rules("max").Apply(tk), rw = rules("row").Apply(tk), cl = rules("col").Apply(tk)
                        If Not (IsSmallInt(mx) AndAlso IsSmallInt(rw) AndAlso IsSmallInt(cl)) Then Continue For
                        Dim tree = ti.Tree
                        If rules.ContainsKey("tree") AndAlso rules("tree").Strong Then
                            Dim tv = rules("tree").Apply(tk)
                            If IsSmallInt(tv) AndAlso CInt(tv) < 3 Then tree = (ti.Tree \ 3) * 3 + CInt(tv)
                        End If
                        If CInt(mx) <> ti.Max OrElse CInt(rw) <> ti.Row OrElse CInt(cl) <> ti.Col OrElse tree <> ti.Tree Then
                            out.Add(New Finding With {.Category = Cat.Talent, .Status = FindStatus.Changed, .Prop = "talent", .Id = id,
                                .Name = known.Names.GetValueOrDefault(id, id), .Extra = $"{known.TreeLabel(tree)}|{mx}|{rw}|{cl}",
                                .Notes = $"was {known.TreeLabel(ti.Tree)}|{ti.Max}|{ti.Row}|{ti.Col} (tree|max rank|row|column)"})
                        End If
                    Next
                End If
            End If

            For Each prop In checks
                Dim rows As New List(Of Finding)
                Dim compared = 0
                For Each id In ids
                    Dim old = known.GetProp(category, id, prop)
                    If old Is Nothing Then Continue For
                    Dim nv = rules(prop).Apply(toksOf(id))
                    If nv Is Nothing OrElse nv.Contains("{"c) Then Continue For   ' {zone}-style text is filled in by the game
                    If prop = "icon" AndAlso Not IsIcon(nv) Then Continue For
                    If (prop = "name") AndAlso Not IsTitle(nv) Then Continue For
                    compared += 1
                    If String.Equals(nv, old, If(prop = "icon", StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)) Then Continue For
                    Dim f As New Finding With {.Category = category, .Status = FindStatus.Changed, .Prop = prop, .Id = id,
                        .Name = known.GetProp(category, id, "name"), .Notes = $"{prop} was: {old}"}
                    Select Case prop
                        Case "icon" : f.Icon = nv
                        Case "name" : f.Name = nv
                        Case "desc" : f.Description = nv
                        Case Else : f.Extra = nv
                    End Select
                    rows.Add(f)
                Next
                ' if a lot "changed", the rule is probably reading the wrong value: don't trust it
                If compared > 0 AndAlso rows.Count <= Math.Max(2, compared \ 4) Then
                    out.AddRange(rows)
                ElseIf rows.Count > 0 Then
                    Log($"  {category}: ignored {rows.Count} possible {prop} changes (too many to be real).")
                End If
            Next
            If out.Count > 0 Then Log($"  {category}: {out.Count} changed value(s).")
            Return out
        End Function

        ''' <summary>Works out tree, max rank, row and column of a new talent.</summary>
        Private Sub PlaceNewTalent(known As KnownData, f As Finding, tk As List(Of Tok), rules As Dictionary(Of String, Rule), offs As Dictionary(Of String, Long))
            ' tree: the known talent written just before it in the game file is normally in the same tree
            Dim prev = offs.Where(Function(p) p.Value < f.Offset).OrderByDescending(Function(p) p.Value).FirstOrDefault()
            If prev.Key Is Nothing Then prev = offs.OrderBy(Function(p) Math.Abs(p.Value - f.Offset)).FirstOrDefault()
            Dim tree = If(prev.Key IsNot Nothing AndAlso known.Talents.ContainsKey(prev.Key), known.Talents(prev.Key).Tree, -1)
            Dim tv = rules.GetValueOrDefault("tree")?.Apply(tk)
            If tree >= 0 AndAlso IsSmallInt(tv) AndAlso CInt(tv) < 3 Then tree = (tree \ 3) * 3 + CInt(tv)
            Dim mx = rules.GetValueOrDefault("max")?.Apply(tk)
            Dim rw = rules.GetValueOrDefault("row")?.Apply(tk)
            Dim cl = rules.GetValueOrDefault("col")?.Apply(tk)
            Dim ok = tree >= 0 AndAlso IsSmallInt(mx) AndAlso IsSmallInt(rw) AndAlso IsSmallInt(cl)
            f.Extra = $"{If(tree >= 0, known.TreeLabel(tree), "?")}|{If(mx, "?")}|{If(rw, "?")}|{If(cl, "?")}"
            f.Include = ok
            f.Notes = If(ok, "tree|max rank|row|column - tree guessed from the talent before it, check it",
                         "could not read its tree position; fill in Extra as tree|max|row|col to add it")
        End Sub
    End Class

End Namespace
