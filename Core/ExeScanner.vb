Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Core

    ''' <summary>A piece of text found in the game file (a whole string, or one quoted literal inside code).</summary>
    Public Structure Field
        Public Offset As Long
        Public Text As String
        Public Sub New(o As Long, t As String)
            Offset = o : Text = t
        End Sub
    End Structure

    Public Class KeyResult
        Public Seal As SaveSeal
        Public Verified As Boolean
        Public Changed As Boolean
        Public Note As String = ""
    End Class

    ''' <summary>
    ''' Reads the game executable the same way it was analysed by hand: pulls out every piece of text,
    ''' then looks for the data tables the editor uses (achievements, zones, pets, tabards, perks, codex,
    ''' talents) by finding where the ids the editor already knows sit in the file, and reading the
    ''' unknown ids that sit among them, each followed by its display name.
    ''' </summary>
    Partial Public Class ExeScanner

        Public ReadOnly Fields As New List(Of Field)
        ''' <summary>id-like word -> offsets. Standalone = the word is a whole string or a quoted literal by itself.</summary>
        Private ReadOnly Words As New Dictionary(Of String, List(Of (Off As Long, Alone As Boolean)))(StringComparer.Ordinal)
        Private ReadOnly HexCands As New List(Of (Off As Long, Hex As String))
        Private ReadOnly ArrayCands As New List(Of (Off As Long, Bytes As Byte()))
        Private ReadOnly Prefixes As New Dictionary(Of String, Long)
        Private Data As Byte()
        ''' <summary>Where each table was found and how its entries look, for the property and gear passes.</summary>
        Private ReadOnly Tables As New Dictionary(Of String, (R As Region, Sh As Shape))
        Private ReadOnly Log As Action(Of String)

        Public Property FileSize As Long

        Public Sub New(Optional log As Action(Of String) = Nothing)
            Me.Log = If(log, Sub(s) Return)
        End Sub

        ' ======================= text extraction =======================

        Private Shared ReadOnly WordRx As New Regex("(?<![A-Za-z0-9_:])([a-z][a-z0-9_]*(?::[a-z0-9_]+)*|9\d{4}|1\d{5})(?![A-Za-z0-9_])", RegexOptions.Compiled)
        Private Shared ReadOnly HexRx As New Regex("(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled)
        Private Shared ReadOnly ArrRx As New Regex("\[\s*((?:(?:0x[0-9a-fA-F]{1,2}|\d{1,3})\s*,\s*){15}(?:0x[0-9a-fA-F]{1,2}|\d{1,3}))\s*,?\s*\]", RegexOptions.Compiled)
        Private Shared ReadOnly PrefixRx As New Regex("azeroth-survivors/save/[A-Za-z0-9_.\-]{1,12}/", RegexOptions.Compiled)

        Public Sub Load(path As String)
            Data = File.ReadAllBytes(path)
            exeFile = path
            FileSize = Data.LongLength
            Log($"Read {IO.Path.GetFileName(path)} ({FileSize / 1048576.0:0.0} MB).")
            If TryLoadPe() Then
                Log($"Compiled Windows program: {Fields.Count:N0} exact strings found through the code and data that use them, {Words.Count:N0} id-like words.")
                Return
            End If
            ScanUtf8()
            ScanUtf16()
            Fields.Sort(Function(a, b) a.Offset.CompareTo(b.Offset))
            Log($"Found {Fields.Count:N0} text strings and {Words.Count:N0} different id-like words.")
        End Sub

        ''' <summary>For testing: scan bytes already in memory.</summary>
        Public Sub LoadBytes(bytes As Byte())
            Data = bytes : FileSize = bytes.LongLength
            If TryLoadPe() Then Return
            ScanUtf8() : ScanUtf16()
            Fields.Sort(Function(a, b) a.Offset.CompareTo(b.Offset))
        End Sub

        Private Shared Function Utf8Len(b As Byte(), i As Integer) As Integer
            Dim c = b(i)
            Dim n As Integer
            If c >= &HC2 AndAlso c <= &HDF Then
                n = 2
            ElseIf c >= &HE0 AndAlso c <= &HEF Then
                n = 3
            ElseIf c >= &HF0 AndAlso c <= &HF4 Then
                n = 4
            Else
                Return 0
            End If
            If i + n > b.Length Then Return 0
            For k = 1 To n - 1
                If (b(i + k) And &HC0) <> &H80 Then Return 0
            Next
            Return n
        End Function

        Private Sub ScanUtf8()
            Dim b = Data, i = 0, n = b.Length
            While i < n
                Dim st = i
                While i < n
                    Dim c = b(i)
                    If (c >= &H20 AndAlso c < &H7F) OrElse c = 9 OrElse c = 10 OrElse c = 13 Then
                        i += 1
                    ElseIf c >= &H80 Then
                        Dim l = Utf8Len(b, i)
                        If l = 0 Then Exit While
                        i += l
                    Else
                        Exit While
                    End If
                End While
                If i - st >= 3 Then
                    ' Latin1 keeps one char per byte, so regex positions are byte offsets
                    ProcessRun(Encoding.Latin1.GetString(b, st, i - st), st, 1, True)
                End If
                If i = st Then i += 1
            End While
        End Sub

        Private Sub ScanUtf16()
            Dim b = Data, n = b.Length - 1, i = 0
            Dim sb As New StringBuilder()
            While i < n
                Dim st = i
                sb.Clear()
                While i < n AndAlso b(i + 1) = 0 AndAlso ((b(i) >= &H20 AndAlso b(i) < &H7F) OrElse b(i) = 9 OrElse b(i) = 10)
                    sb.Append(ChrW(b(i))) : i += 2
                End While
                If sb.Length >= 4 Then
                    ProcessRun(sb.ToString(), st, 2, False)
                Else
                    i = st + 1
                End If
            End While
        End Sub

        ''' <summary>Looks like program source (JavaScript, JSON...) rather than a single plain string.</summary>
        Private Shared Function LooksLikeCode(s As String) As Boolean
            If s.Length < 40 Then Return False
            Dim q = 0, punct = 0
            For Each ch In s
                If ch = """"c OrElse ch = "`"c Then q += 1
                If ch = "{"c OrElse ch = "}"c OrElse ch = "["c OrElse ch = "]"c OrElse ch = ";"c OrElse ch = "="c Then punct += 1
            Next
            Return q >= 4 AndAlso punct >= 2
        End Function

        Private Shared Function Utf8Text(latin As String, utf8 As Boolean) As String
            If Not utf8 Then Return latin
            Return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(latin))
        End Function

        Private Sub ProcessRun(run As String, baseOff As Long, scale As Integer, utf8 As Boolean)
            Dim spans As New List(Of (S As Integer, E As Integer))
            If LooksLikeCode(run) Then
                Dim j = 0
                While j < run.Length
                    Dim c = run(j)
                    If c = """"c OrElse c = "'"c OrElse c = "`"c Then
                        Dim k = j + 1
                        While k < run.Length AndAlso run(k) <> c AndAlso run(k) <> vbLf(0)
                            If run(k) = "\"c Then k += 1
                            k += 1
                        End While
                        If k < run.Length AndAlso run(k) = c Then
                            spans.Add((j + 1, k))
                            Dim txt = Unescape(run.Substring(j + 1, k - j - 1))
                            If txt.Length > 0 Then Fields.Add(New Field(baseOff + (j + 1) * scale, Utf8Text(txt, utf8)))
                            j = k + 1
                            Continue While
                        End If
                    End If
                    j += 1
                End While
            Else
                Dim t = run.Trim()
                If t.Length >= 2 Then Fields.Add(New Field(baseOff + run.IndexOf(t(0)) * scale, Utf8Text(t, utf8)))
            End If

            ' id-like words
            Dim trimmed = run.Trim()
            Dim si = 0
            For Each m As Match In WordRx.Matches(run)
                Dim w = m.Value
                If w.Length > 60 Then Continue For
                Dim alone As Boolean
                If spans.Count > 0 Then
                    While si < spans.Count AndAlso spans(si).E < m.Index : si += 1 : End While
                    If si < spans.Count AndAlso spans(si).S <= m.Index AndAlso m.Index < spans(si).E Then
                        alone = (spans(si).E - spans(si).S) = w.Length   ' the literal is exactly this word
                    Else
                        alone = True                                      ' bare word in code, e.g. an object key
                    End If
                Else
                    alone = (trimmed = w)
                End If
                Dim lst As List(Of (Off As Long, Alone As Boolean)) = Nothing
                If Not Words.TryGetValue(w, lst) Then lst = New List(Of (Off As Long, Alone As Boolean)) : Words(w) = lst
                If lst.Count < 400 Then lst.Add((baseOff + m.Index * scale, alone))
                Dim ci = w.IndexOf(":"c)
                If ci > 0 AndAlso Char.IsDigit(w(w.Length - 1)) Then
                    Dim b = w.Substring(0, ci)
                    Dim l2 As List(Of (Off As Long, Alone As Boolean)) = Nothing
                    If Not Words.TryGetValue(b, l2) Then l2 = New List(Of (Off As Long, Alone As Boolean)) : Words(b) = l2
                    If l2.Count < 400 Then l2.Add((baseOff + m.Index * scale, False))
                End If
            Next

            ' seal key material
            If run.Contains("azeroth-survivors/save/") Then
                For Each m As Match In PrefixRx.Matches(run)
                    If Not Prefixes.ContainsKey(m.Value) Then Prefixes(m.Value) = baseOff + m.Index * scale
                Next
                If Not Prefixes.Values.Any(Function(v) v >= baseOff AndAlso v < baseOff + run.Length * scale) Then
                    Dim p = run.IndexOf("azeroth-survivors/save/")
                    Prefixes.TryAdd("azeroth-survivors/save/", baseOff + p * scale)
                End If
            End If
            If run.Length >= 32 Then
                For Each m As Match In HexRx.Matches(run)
                    If HexCands.Count < 5000 Then HexCands.Add((baseOff + m.Index * scale, m.Value.ToLowerInvariant()))
                Next
                If run.Length >= 40 AndAlso run.Contains("["c) Then
                    For Each m As Match In ArrRx.Matches(run)
                        Try
                            Dim parts = m.Groups(1).Value.Split(","c).Select(Function(x) x.Trim()).ToArray()
                            Dim bs = parts.Select(Function(x) CByte(If(x.StartsWith("0x"), Convert.ToInt32(x, 16), Integer.Parse(x)))).ToArray()
                            ArrayCands.Add((baseOff + m.Index * scale, bs))
                        Catch
                        End Try
                    Next
                End If
            End If
        End Sub

        Private Shared Function Unescape(s As String) As String
            If s.IndexOf("\"c) < 0 Then Return s
            Return s.Replace("\'", "'").Replace("\""", """").Replace("\n", " ").Replace("\\", "\")
        End Function

        ' ======================= helpers =======================

        Private Shared ReadOnly IconRx As New Regex("^(?:[A-Z][A-Za-z]+|classicon)_[A-Za-z0-9_]+$", RegexOptions.Compiled)
        Public Shared Function IsIcon(s As String) As Boolean
            Return s.Length <= 64 AndAlso IconRx.IsMatch(s)
        End Function

        Public Shared Function IsTitle(s As String) As Boolean
            If s.Length < 2 OrElse s.Length > 60 Then Return False
            If Not (Char.IsUpper(s(0)) OrElse Char.IsDigit(s(0))) Then Return False
            If s.EndsWith(".") OrElse IsIcon(s) Then Return False
            If s.IndexOfAny("{}=;<>()[]\|/#@$%^*~`".ToCharArray()) >= 0 Then Return False
            If s.Contains("  ") OrElse s.Contains(vbLf) Then Return False
            If Not s.Any(AddressOf Char.IsLower) AndAlso s.Length > 4 Then Return False    ' ALL CAPS constants
            If s.Contains("_"c) Then Return False
            Return s.Count(AddressOf Char.IsLetter) >= 2
        End Function

        Private Shared ReadOnly PlaceholderRx As New Regex("\{[a-z_]+\}", RegexOptions.Compiled)

        ''' <summary>Game text with {zone}-style blanks made readable ("Beat {zone}..." -> "Beat a zone...").</summary>
        Public Shared Function FillPlaceholders(s As String) As String
            If s Is Nothing OrElse s.IndexOf("{"c) < 0 Then Return s
            Return PlaceholderRx.Replace(s, Function(m)
                                                Select Case m.Value
                                                    Case "{zone}" : Return "a zone"
                                                    Case "{class}" : Return "a class"
                                                    Case "{boss}" : Return "the final boss"
                                                    Case "{n}" : Return "N"
                                                    Case Else : Return m.Value.Trim("{"c, "}"c)
                                                End Select
                                            End Function)
        End Function

        Public Shared Function IsSentence(s As String) As Boolean
            s = PlaceholderRx.Replace(s, "x")
            If s.Length < 8 OrElse s.Length > 260 Then Return False
            If Not s.Contains(" "c) Then Return False
            If Not (Char.IsUpper(s(0)) OrElse Char.IsDigit(s(0))) Then Return False
            If s.IndexOfAny("{}=;<>\|#@$^*~`".ToCharArray()) >= 0 Then Return False
            Dim last = s.TrimEnd()(s.TrimEnd().Length - 1)
            Return last = "."c OrElse last = "!"c OrElse last = ")"c OrElse last = "?"c
        End Function

        Private Function FirstFieldAfter(off As Long) As Integer
            Dim lo = 0, hi = Fields.Count
            While lo < hi
                Dim mid = (lo + hi) \ 2
                If Fields(mid).Offset <= off Then lo = mid + 1 Else hi = mid
            End While
            Return lo
        End Function

        ''' <summary>Fields that start after a word (skipping the word's own literal), within maxDist bytes.</summary>
        Private Iterator Function After(off As Long, word As String, maxCount As Integer, maxDist As Long) As IEnumerable(Of Field)
            Dim i = FirstFieldAfter(off), n = 0
            While i < Fields.Count AndAlso n < maxCount
                Dim f = Fields(i)
                If f.Offset - off > maxDist Then Exit While
                If f.Text <> word Then Yield f : n += 1
                i += 1
            End While
        End Function

        Public Function Has(word As String) As Boolean
            Return Words.ContainsKey(word) OrElse InlineSet.Contains(word) OrElse Generated.Contains(word)
        End Function

        Private Function Occ(word As String) As List(Of (Off As Long, Alone As Boolean))
            Dim l As List(Of (Off As Long, Alone As Boolean)) = Nothing
            Words.TryGetValue(word, l)
            Return l
        End Function

        ''' <summary>Where a table sits. A table can be split in parts (e.g. one talent list per class).</summary>
        Private Class Region
            Public Spans As New List(Of (S As Long, E As Long))
            Public Count As Integer, Total As Integer
            Public Function Contains(o As Long) As Boolean
                For Each sp In Spans
                    If o >= sp.S AndAlso o <= sp.E Then Return True
                Next
                Return False
            End Function
        End Class

        ''' <summary>Finds the stretches of the file where the given known ids sit close together.</summary>
        Private Function FindRegion(ids As IEnumerable(Of String), Optional maxGap As Long = 3000) As Region
            Dim pts As New List(Of (Off As Long, Id As String))
            Dim total = 0
            For Each id In ids
                total += 1
                Dim l = Occ(id)
                If l Is Nothing Then Continue For
                For Each o In l
                    If o.Alone Then pts.Add((o.Off, id))
                Next
            Next
            If pts.Count = 0 Then Return Nothing
            pts.Sort(Function(a, b) a.Off.CompareTo(b.Off))
            Dim chains As New List(Of (S As Long, E As Long, Ids As HashSet(Of String)))
            Dim st = 0
            For i = 0 To pts.Count
                If i = pts.Count OrElse (i > st AndAlso pts(i).Off - pts(i - 1).Off > maxGap) Then
                    Dim seen As New HashSet(Of String)
                    For k = st To i - 1 : seen.Add(pts(k).Id) : Next
                    chains.Add((pts(st).Off, pts(i - 1).Off, seen))
                    st = i
                End If
            Next
            ' the biggest part, plus any other part that holds at least 3 known ids
            Dim best = chains.OrderByDescending(Function(c) c.Ids.Count).First()
            If best.Ids.Count < 3 Then Return Nothing
            Dim r As New Region With {.Total = total}
            Dim all As New HashSet(Of String)
            For Each c In chains
                If c.Ids.Count < 3 AndAlso Not c.Equals(best) Then Continue For
                Dim pad = Math.Min(4096L, Math.Max(256L, (c.E - c.S) \ Math.Max(1, c.Ids.Count) * 3))
                r.Spans.Add((c.S - pad, c.E + pad))
                all.UnionWith(c.Ids)
            Next
            r.Count = all.Count
            If r.Count < Math.Max(3, CInt(total * 0.3)) Then Return Nothing
            Return r
        End Function

        ''' <summary>Compiled games build some ids at run time (e.g. "zone9:" + zone), so a "missing" id only means something when nearly all ids were found.</summary>
        Private Function MissingReliable(ids As ICollection(Of String)) As Boolean
            If Not PeMode Then Return True
            Return ids.Count > 0 AndAlso ids.Where(AddressOf Has).Count() >= ids.Count * 0.9
        End Function

        Private Shared ReadOnly Stop_ As New HashSet(Of String)(("id name desc description icon rarity max min cost type true false null undefined const let var function return this new if else for while do key value label title text color kind slot tier class race zone " &
            "tab cat category get set has add map filter push pop length count level rank ranks row col column hp mp gold xp exp hero heroes item items pet pets perk perks tabard tabards codex default " &
            "src img png jpg webp ogg mp3 wav json data info note notes tooltip tip size width height left right top bottom x y z dx dy speed time delay duration damage heal radius range " &
            "chance rate mult bonus stats stat scale index idx list arr obj str num int float bool void async await yield break continue case switch try catch throw typeof instanceof in of " &
            "export import from as with static public private protected extends super delete prototype constructor string number boolean object symbol bigint math date error console log warn " &
            "unlock unlocks open seen ach achat spec equip look talent beat resets races marks pity glow hbeat stars pick").Split(" "c, StringSplitOptions.RemoveEmptyEntries))

        ' ---------- learning how a table is laid out ----------

        ''' <summary>How entries of one table look in the file, learnt from entries the editor already knows.</summary>
        Private Class Shape
            Public Pre As String, Post As String
            Public TitleIdx As Integer = Integer.MinValue, DescIdx As Integer = Integer.MinValue, IconIdx As Integer = Integer.MinValue
            Public Samples As Integer
            Public Overrides Function ToString() As String
                Return $"{Samples} samples, before={Show(Pre)}, after={Show(Post)}, name {P(TitleIdx)}, text {P(DescIdx)}, icon {P(IconIdx)}"
            End Function
            Private Shared Function P(i As Integer) As String
                Return If(i = Integer.MinValue, "?", i.ToString("+0;-0"))
            End Function
            Private Shared Function Show(s As String) As String
                If s Is Nothing Then Return "(varies)"
                Return "'" & New String(s.Select(Function(c) If(Char.IsControl(c), "·"c, c)).ToArray()) & "'"
            End Function
        End Class

        Private Function Ctx(off As Long, len As Integer, wordLen As Integer, before As Boolean) As String
            Dim s = If(before, off - len, off + wordLen)
            If s < 0 OrElse s + len > Data.LongLength Then Return ""
            Return Encoding.Latin1.GetString(Data, CInt(s), len)
        End Function

        ''' <summary>Longest context (4..1 bytes) shared by at least 70% of the samples.</summary>
        Private Function CommonCtx(samples As List(Of (Off As Long, Id As String)), before As Boolean) As String
            For n = 4 To 1 Step -1
                Dim l = n
                Dim top = samples.GroupBy(Function(x) Ctx(x.Off, l, x.Id.Length, before)).OrderByDescending(Function(g) g.Count()).First()
                If top.Count() >= samples.Count * 0.7 Then Return top.Key
            Next
            Return Nothing
        End Function

        Private Const NoIdx As Integer = Integer.MinValue

        ''' <summary>Strings before an id (nearest first), for compiled programs where the name can come first.</summary>
        Private Function Before(off As Long, word As String, maxCount As Integer, maxDist As Long) As List(Of Field)
            Dim l As New List(Of Field)
            Dim i = FirstFieldAfter(off) - 1
            While i >= 0 AndAlso l.Count < maxCount
                Dim f = Fields(i)
                If off - f.Offset > maxDist Then Exit While
                If f.Offset <> off AndAlso f.Text <> word Then l.Add(f)
                i -= 1
            End While
            Return l
        End Function

        ''' <summary>Field at a signed position: 0,1,2.. after the id; -1,-2.. before it.</summary>
        Private Shared Function AtIdx(nxt As List(Of Field), prv As List(Of Field), i As Integer) As Field?
            If i >= 0 Then Return If(i < nxt.Count, nxt(i), CType(Nothing, Field?))
            Dim k = -i - 1
            Return If(k < prv.Count, prv(k), CType(Nothing, Field?))
        End Function

        Private Shared Function IdxOf(nxt As List(Of Field), prv As List(Of Field), text As String) As Integer
            Dim i = nxt.FindIndex(Function(f) f.Text = text)
            If i >= 0 Then Return i
            i = prv.FindIndex(Function(f) f.Text = text)
            Return If(i >= 0, -i - 1, NoIdx)
        End Function

        ''' <summary>
        ''' The occurrence of a known id that is its table entry: inside the region and, when an id is used in
        ''' several places (a zone id also appears in save code and co-op achievements), the one next to its name.
        ''' </summary>
        Private Function BestOcc(id As String, r As Region, known As KnownData) As Long
            Dim l = Occ(id)
            If l Is Nothing Then Return -1
            Dim first = -1L
            Dim nm As String = Nothing
            known.Names.TryGetValue(id, nm)
            For Each o In l
                If Not o.Alone OrElse Not r.Contains(o.Off) Then Continue For
                If first < 0 Then first = o.Off
                If nm Is Nothing Then Exit For
                Dim nxt = After(o.Off, id, 3, 900).ToList()
                Dim prv = If(PeMode, Before(o.Off, id, 3, 900), New List(Of Field))
                If IdxOf(nxt, prv, nm) <> NoIdx Then Return o.Off
            Next
            Return first
        End Function

        Private Function LearnShape(ids As IEnumerable(Of String), r As Region, known As KnownData) As Shape
            Dim sh As New Shape()
            Dim samples As New List(Of (Off As Long, Id As String))
            For Each id In ids
                Dim o = BestOcc(id, r, known)
                If o >= 0 Then samples.Add((o, id))
            Next
            sh.Samples = samples.Count
            If samples.Count < 3 Then Return sh
            sh.Pre = CommonCtx(samples, True)
            sh.Post = CommonCtx(samples, False)
            Dim ti As New List(Of Integer), di As New List(Of Integer), ii As New List(Of Integer)
            For Each smp In samples
                Dim nxt = After(smp.Off, smp.Id, 6, 900).ToList()
                Dim prv = If(PeMode, Before(smp.Off, smp.Id, 4, 900), New List(Of Field))
                Dim nm As String = Nothing, ds As String = Nothing
                If known.Names.TryGetValue(smp.Id, nm) Then
                    Dim i = IdxOf(nxt, prv, nm)
                    If i <> NoIdx Then ti.Add(i)
                End If
                If known.Descs.TryGetValue(smp.Id, ds) Then
                    Dim i = IdxOf(nxt, prv, ds)
                    If i <> NoIdx Then di.Add(i)
                End If
                Dim ic = nxt.FindIndex(Function(f) IsIcon(f.Text))
                If ic >= 0 AndAlso ic <= 3 Then ii.Add(ic)
            Next
            If ii.Count >= samples.Count * 0.7 Then
                Dim g = ii.GroupBy(Function(x) x).OrderByDescending(Function(x) x.Count()).First()
                If g.Count() >= samples.Count * 0.7 Then sh.IconIdx = g.Key
            End If
            If ti.Count >= 3 Then
                Dim g = ti.GroupBy(Function(x) x).OrderByDescending(Function(x) x.Count()).First()
                If g.Count() >= ti.Count * 0.6 Then sh.TitleIdx = g.Key
            End If
            If di.Count >= 3 Then
                Dim g = di.GroupBy(Function(x) x).OrderByDescending(Function(x) x.Count()).First()
                If g.Count() >= di.Count * 0.6 Then sh.DescIdx = g.Key
            End If
            Return sh
        End Function

        ''' <summary>Does an occurrence of word w look like an entry of the table? Returns its name (and text).</summary>
        Private Function Fits(sh As Shape, w As String, off As Long, needDesc As Boolean, ByRef title As String, ByRef desc As String, ByRef nxt As List(Of Field)) As Boolean
            title = Nothing : desc = Nothing
            If sh.Pre IsNot Nothing AndAlso Ctx(off, sh.Pre.Length, w.Length, True) <> sh.Pre Then Return False
            If sh.Post IsNot Nothing AndAlso Ctx(off, sh.Post.Length, w.Length, False) <> sh.Post Then Return False
            nxt = After(off, w, 6, 900).ToList()
            Dim prv = If(PeMode, Before(off, w, 4, 900), New List(Of Field))
            Dim ti As Integer
            If sh.TitleIdx <> NoIdx Then
                ti = sh.TitleIdx
                Dim f = AtIdx(nxt, prv, ti)
                If f Is Nothing OrElse Not IsTitle(f.Value.Text) Then Return False
            Else
                ti = nxt.FindIndex(Function(f) IsTitle(f.Text))
                If ti < 0 OrElse ti > 1 OrElse nxt(ti).Offset - off > 300 Then Return False
            End If
            title = AtIdx(nxt, prv, ti).Value.Text
            ' tables whose entries all have an icon at the same place: a new entry must have one there too
            If sh.IconIdx <> NoIdx Then
                Dim icf = AtIdx(nxt, prv, sh.IconIdx)
                If icf Is Nothing OrElse Not IsIcon(icf.Value.Text) Then Return False
            End If
            If needDesc Then
                Dim d = If(sh.DescIdx <> NoIdx, sh.DescIdx, If(ti >= 0, ti + 1, ti - 1))
                Dim f = AtIdx(nxt, prv, d)
                If f IsNot Nothing AndAlso IsSentence(f.Value.Text) Then desc = f.Value.Text
            End If
            Return True
        End Function

        ''' <summary>New id-like words inside the region that look like entries of the same table.</summary>
        Private Function NewIdsIn(r As Region, sh As Shape, known As KnownData, numeric As Boolean, Optional recordSpans As List(Of (Lo As Long, Hi As Long)) = Nothing) As List(Of (Id As String, Off As Long, Name As String))
            Dim out As New List(Of (Id As String, Off As Long, Name As String))
            For Each kv In Words
                Dim w = kv.Key
                If numeric <> Char.IsDigit(w(0)) Then Continue For
                If numeric AndAlso Not (w.Length = 5 AndAlso w(0) = "9"c) Then Continue For
                If known.AllIds.Contains(w) OrElse Stop_.Contains(w) OrElse w.Contains(":"c) Then Continue For
                If Not numeric AndAlso w.Length < 3 Then Continue For
                For Each o In kv.Value
                    If Not o.Alone OrElse Not r.Contains(o.Off) Then Continue For
                    ' compiled game whose table is a list of records: a new entry must be one of those records too
                    If recordSpans IsNot Nothing AndAlso Not InRecords(o.Off, recordSpans) Then Continue For
                    Dim t As String = Nothing, d As String = Nothing, nx As List(Of Field) = Nothing
                    If Fits(sh, w, o.Off, False, t, d, nx) Then
                        out.Add((w, o.Off, t))
                        Exit For
                    End If
                Next
            Next
            out.Sort(Function(a, b) a.Off.CompareTo(b.Off))
            Return out
        End Function

        Private Shared Function Pretty(id As String) As String
            Dim s = id.Substring(id.LastIndexOf(":"c) + 1).Replace("_", " ")
            Return If(s.Length > 0, Char.ToUpper(s(0)) & s.Substring(1), id)
        End Function

        ' ======================= the analysis =======================

        Public Function Analyse(known As KnownData) As List(Of Finding)
            Dim res As New List(Of Finding)
            PrepareGenerated(known)
            res.AddRange(Achievements(known))
            res.AddRange(Zones(known, res))
            If PeMode Then res.AddRange(Simple(known, Cat.Zone, False))
            res.AddRange(TemplateAchievements(known, res))
            res.AddRange(TitleAchievements(known, res))
            res.AddRange(Simple(known, Cat.Pet, False))
            res.AddRange(If(PeMode, StructTabards(known), Simple(known, Cat.Tabard, True)))
            res.AddRange(Simple(known, Cat.Perk, False))
            res.AddRange(Simple(known, Cat.Jewelry, False))
            res.AddRange(Simple(known, Cat.Armory, False))
            res.AddRange(Simple(known, Cat.Talent, False))
            res.AddRange(Simple(known, Cat.Ability, False))
            res.AddRange(ReadProperties(known, res))
            res.AddRange(GearScan(known))

            ' a zone id also shows up as a field of co-op achievements: it is not an achievement itself
            Dim zoneIds = New HashSet(Of String)(known.IdsOf(Cat.Zone).Concat(res.Where(Function(f) f.Category = Cat.Zone).Select(Function(f) f.Id)))
            res.RemoveAll(Function(f) f.Category = Cat.Achievement AndAlso f.Status = FindStatus.New AndAlso zoneIds.Contains(f.Id))

            ' an id can only be new once: keep the category whose table it sits in first
            Dim seen As New HashSet(Of String)
            Dim final As New List(Of Finding)
            For Each f In res
                If f.Status = FindStatus.New Then
                    If Not seen.Add(If(f.Category = Cat.Zone, "zone>" & f.Id, f.Id)) Then Continue For
                End If
                final.Add(f)
            Next
            Return final
        End Function

        Private Function Achievements(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim ids = known.IdsOf(Cat.Achievement)
            Dim found = ids.Where(AddressOf Has).Count()
            Log($"Achievements: {found} of {ids.Count} known ids are in the game file.")
            Dim region = FindRegion(ids)
            If region Is Nothing Then Log("  Could not locate the achievement table.")

            ' which tab each prefix belongs to, learnt from the editor
            Dim tabOf As New Dictionary(Of String, String)
            For Each g In known.AchInfo.GroupBy(Function(p) PrefixOf(p.Key))
                tabOf(g.Key) = g.GroupBy(Function(p) p.Value.Tab).OrderByDescending(Function(x) x.Count()).First().Key
            Next
            Dim knownPrefixes = New HashSet(Of String)(ids.Where(Function(x) x.Contains(":"c)).Select(AddressOf PrefixOf))
            ' known achievement offsets, for "nearest neighbour" tab guesses
            Dim anchors As New List(Of (Off As Long, Id As String))
            For Each id In ids
                Dim l = Occ(id)
                If l IsNot Nothing Then anchors.AddRange(l.Where(Function(o) o.Alone).Select(Function(o) (o.Off, id)))
            Next
            anchors.Sort(Function(a, b) a.Off.CompareTo(b.Off))

            Dim sh = If(region IsNot Nothing, LearnShape(ids, region, known), New Shape())
            Log("  Layout: " & sh.ToString())
            Dim knownTitles As New HashSet(Of String)(ids.Select(Function(i) known.GetProp(Cat.Achievement, i, "name")).Where(Function(x) x IsNot Nothing))
            If region IsNot Nothing Then Tables(Cat.Achievement) = (region, sh)

            For Each kv In Words
                Dim w = kv.Key
                If ids.Contains(w) OrElse Char.IsDigit(w(0)) Then Continue For
                Dim pre = PrefixOf(w)
                Dim prefixed = w.Contains(":"c) AndAlso knownPrefixes.Contains(pre)
                If Not prefixed AndAlso (Stop_.Contains(w) OrElse known.AllIds.Contains(w)) Then Continue For
                For Each o In kv.Value
                    If Not o.Alone Then Continue For
                    Dim inTable = region IsNot Nothing AndAlso region.Contains(o.Off)
                    If Not prefixed AndAlso Not inTable Then Continue For
                    Dim title As String = Nothing, desc As String = Nothing, nxt As List(Of Field) = Nothing
                    If Not Fits(sh, w, o.Off, True, title, desc, nxt) Then Continue For
                    If desc Is Nothing AndAlso (Not prefixed OrElse PeMode) Then Continue For
                    If Not prefixed AndAlso w.Length < 3 Then Continue For
                    ' a title the editor already has belongs to an achievement it already has
                    If knownTitles.Contains(title) Then Continue For
                    Dim icon = nxt.FirstOrDefault(Function(f) IsIcon(f.Text)).Text
                    Dim tab As String = Nothing
                    If Not tabOf.TryGetValue(pre, tab) Then tab = NearestTab(anchors, known, o.Off)
                    out.Add(New Finding With {
                        .Category = Cat.Achievement, .Id = w, .Name = title, .Description = If(desc, ""),
                        .Icon = If(icon, "INV_Misc_QuestionMark"), .Extra = If(tab, "Feats"),
                        .Include = desc IsNot Nothing, .Offset = o.Off,
                        .Notes = If(desc IsNot Nothing, If(inTable, "", "found outside the achievement table"), "no description found - check before adding")})
                    Exit For
                Next
            Next
            out.Sort(Function(a, b) a.Offset.CompareTo(b.Offset))
            For Each id In If(MissingReliable(ids), ids, New HashSet(Of String))
                If Not Has(id) Then
                    Dim f = New Finding With {.Category = Cat.Achievement, .Status = FindStatus.Missing, .Id = id, .Include = False,
                        .Name = known.AchInfo(id).Title, .Notes = "in the editor but not in this game file (removed or renamed?)"}
                    Dim ren = out.FirstOrDefault(Function(x) x.Id.StartsWith(id))
                    If ren IsNot Nothing Then f.Notes = "probably renamed to " & ren.Id
                    out.Add(f)
                End If
            Next
            Log($"  {out.Where(Function(x) x.Status = FindStatus.New).Count()} new, {out.Where(Function(x) x.Status = FindStatus.Missing).Count()} missing.")
            Return out
        End Function

        Private Shared Function PrefixOf(id As String) As String
            Dim i = id.IndexOf(":"c)
            Return If(i < 0, id, id.Substring(0, i))
        End Function

        Private Shared Function NearestTab(anchors As List(Of (Off As Long, Id As String)), known As KnownData, off As Long) As String
            If anchors.Count = 0 Then Return "Feats"
            Dim best = anchors.OrderBy(Function(a) Math.Abs(a.Off - off)).First()
            Return known.AchInfo(best.Id).Tab
        End Function

        Private Function Zones(known As KnownData, sofar As List(Of Finding)) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim zs = known.IdsOf(Cat.Zone)
            For Each a In sofar.Where(Function(x) x.Category = Cat.Achievement AndAlso x.Status = FindStatus.New AndAlso
                                          (x.Id.StartsWith("zone:") OrElse x.Id.StartsWith("zone9:")))
                Dim z = a.Id.Substring(a.Id.IndexOf(":"c) + 1)
                If zs.Contains(z) OrElse out.Any(Function(f) f.Id = z) Then Continue For
                out.Add(New Finding With {.Category = Cat.Zone, .Id = z,
                    .Name = If(a.Id.StartsWith("zone:"), a.Name, Pretty(z)), .Offset = a.Offset,
                    .Notes = "added after " & If(known.ZoneOrder.Count > 0, known.ZoneOrder.Last(), "the last zone") & " - zone order decides what unlocks next"})
            Next
            ' fix names that came from zone9 first
            For Each z In out
                Dim main = sofar.FirstOrDefault(Function(x) x.Id = "zone:" & z.Id)
                If main IsNot Nothing Then z.Name = main.Name
            Next
            For Each z In If(MissingReliable(known.IdsOf(Cat.Achievement)), zs, New HashSet(Of String))
                If Not Has(z) AndAlso Not Has("zone:" & z) Then
                    out.Add(New Finding With {.Category = Cat.Zone, .Status = FindStatus.Missing, .Id = z, .Include = False,
                        .Notes = "in the editor but not in this game file"})
                End If
            Next
            Log($"Zones: {out.Where(Function(x) x.Status = FindStatus.New).Count()} new.")
            Return out
        End Function

        Private Function Simple(known As KnownData, category As String, numeric As Boolean) As List(Of Finding)
            Dim out As New List(Of Finding)
            Dim ids = known.IdsOf(category)
            If ids.Count = 0 Then Return out
            Dim found = ids.Where(AddressOf Has).Count()
            ' zone entries carry long descriptions, so they sit further apart
            Dim r = FindRegion(ids, If(category = Cat.Zone, 12000, 3000))
            If r Is Nothing Then
                Log($"{category}: {found} of {ids.Count} known ids are in the game file; table not located.")
                Return out
            End If
            Dim sh = LearnShape(ids, r, known)
            Log($"  {category} layout: {sh}")
            Tables(category) = (r, sh)
            Dim recs = If(PeMode, RecordSpans(ids), Nothing)
            ' a record table is checked through its records; a shared icon string can sit elsewhere in the file
            If recs IsNot Nothing Then sh.IconIdx = NoIdx
            For Each c In If(category = Cat.Ability, New List(Of (Id As String, Off As Long, Name As String)), NewIdsIn(r, sh, known, numeric, recs))
                Dim f As New Finding With {.Category = category, .Id = c.Id, .Name = c.Name, .Offset = c.Off}
                Select Case category
                    Case Cat.Perk : f.Extra = "1" : f.Notes = "check the max rank"
                    Case Cat.Armory : f.Extra = NearestRarity(known, c.Off).ToString()
                    Case Cat.Zone
                        ' the game lists its zones in unlock order: put the new one after the zone before it
                        Dim prevZone = known.ZoneOrder.Select(Function(z) (Z:=z, O:=BestOcc(z, r, known))).Where(Function(x) x.O >= 0 AndAlso x.O < c.Off).OrderByDescending(Function(x) x.O).FirstOrDefault()
                        If prevZone.Z IsNot Nothing Then
                            f.Path = "after:" & prevZone.Z
                            f.Notes = "goes after " & known.Names.GetValueOrDefault(prevZone.Z, prevZone.Z) & ", as in the game's zone list (zone order decides what unlocks next)"
                        Else
                            f.Notes = "added after " & If(known.ZoneOrder.Count > 0, known.ZoneOrder.Last(), "the last zone") & " - zone order decides what unlocks next"
                        End If
                    Case Cat.Ability : f.Include = False : f.Notes = "new ability: listed only (the editor keeps abilities per class in the codex)"
                End Select
                out.Add(f)
            Next
            For Each id In If(MissingReliable(ids), ids, New HashSet(Of String))
                If Not Has(id) Then
                    out.Add(New Finding With {.Category = category, .Status = FindStatus.Missing, .Id = id, .Include = False,
                        .Notes = "in the editor but not in this game file"})
                End If
            Next
            Log($"{category}: {found} of {ids.Count} known ids found; {out.Where(Function(x) x.Status = FindStatus.New).Count()} new.")
            Return out
        End Function

        Private Function NearestRarity(known As KnownData, off As Long) As Integer
            Dim best = -1L, rar = 3
            For Each kv In known.ArmoryRarity
                Dim l = Occ(kv.Key)
                If l Is Nothing Then Continue For
                For Each o In l
                    Dim d = Math.Abs(o.Off - off)
                    If best < 0 OrElse d < best Then best = d : rar = kv.Value
                Next
            Next
            Return rar
        End Function

        ' ======================= seal key =======================

        Public Function FindKey(current As SaveSeal, saveText As String) As KeyResult
            Dim res As New KeyResult With {.Seal = current}
            Dim prefList As New List(Of String)
            For Each p In Prefixes.Keys.Where(Function(x) x.EndsWith("/") AndAlso x.Length > "azeroth-survivors/save/".Length)
                prefList.Add(p)
            Next
            If current IsNot Nothing AndAlso Not prefList.Contains(current.Prefix) Then prefList.Add(current.Prefix)
            If prefList.Count = 0 Then prefList.Add("azeroth-survivors/save/v1/")
            Dim exePrefix = Prefixes.Keys.FirstOrDefault(Function(x) x.EndsWith("/") AndAlso x.Length > 23)

            ' candidate secrets, best guesses first
            Dim cands As New List(Of (Bytes As Byte(), From As String))
            ' compiled game: the HMAC key is stored pre-mixed with the HMAC pads (key xor 0x36 / 0x5C)
            For Each pk In PaddedKeys()
                If Not prefList.Contains(pk.Prefix) Then prefList.Insert(0, pk.Prefix)
                cands.Insert(0, (pk.Secret, "HMAC key block at 0x" & pk.Off.ToString("X")))
                If exePrefix Is Nothing Then exePrefix = pk.Prefix
                If Not HexCands.Any(Function(h) h.Hex = Convert.ToHexString(pk.Secret).ToLowerInvariant()) Then HexCands.Add((pk.Off, Convert.ToHexString(pk.Secret).ToLowerInvariant()))
            Next
            Log("Seal prefix in game file: " & If(exePrefix, "(not found)"))
            If current IsNot Nothing Then cands.Add((current.Secret, "current editor key"))
            Dim pOff = If(Prefixes.Count > 0, Prefixes.Values.Min(), -1L)
            For Each h In HexCands.OrderBy(Function(x) If(pOff < 0, 0, Math.Abs(x.Off - pOff)))
                cands.Add((Convert.FromHexString(h.Hex), "hex text at 0x" & h.Off.ToString("X")))
            Next
            For Each a In ArrayCands.OrderBy(Function(x) If(pOff < 0, 0, Math.Abs(x.Off - pOff)))
                cands.Add((a.Bytes, "byte list at 0x" & a.Off.ToString("X")))
            Next
            For Each po In Prefixes.Values
                For d = -2048 To 2048
                    Dim s = po + d
                    If s < 0 OrElse s + 16 > Data.LongLength Then Continue For
                    Dim bs(15) As Byte
                    Array.Copy(Data, s, bs, 0, 16)
                    cands.Add((bs, "raw bytes at 0x" & s.ToString("X")))
                Next
            Next

            If Not String.IsNullOrWhiteSpace(saveText) Then
                For Each p In prefList
                    For Each c In cands
                        Dim s = New SaveSeal(p, c.Bytes)
                        If s.Verifies(saveText) Then
                            res.Seal = s : res.Verified = True
                            res.Changed = current Is Nothing OrElse s.Prefix <> current.Prefix OrElse Not s.Secret.SequenceEqual(current.Secret)
                            res.Note = "Key confirmed with your save (" & c.From & ")."
                            Log(res.Note)
                            Return res
                        End If
                    Next
                Next
                res.Note = "No key in the game file opens your save. Make sure the save was made by this version of the game."
                Log(res.Note)
                Return res
            End If

            ' no save to test with: fall back to what the file shows
            If current IsNot Nothing AndAlso HexCands.Any(Function(h) h.Hex = current.SecretHex) AndAlso (exePrefix Is Nothing OrElse exePrefix = current.Prefix) Then
                res.Note = "The editor's key is still in the game file (pick a save to confirm it)."
            ElseIf HexCands.Count > 0 AndAlso exePrefix IsNot Nothing Then
                Dim h = HexCands.OrderBy(Function(x) Math.Abs(x.Off - pOff)).First()
                res.Seal = SaveSeal.FromHex(exePrefix, h.Hex)
                res.Changed = current Is Nothing OrElse res.Seal.SecretHex <> current.SecretHex OrElse exePrefix <> current.Prefix
                res.Note = If(res.Changed, "The key looks different but could not be confirmed. Pick a save made by this game version to confirm it.",
                              "The editor's key is still in the game file.")
            Else
                res.Note = "Could not tell whether the key changed. Pick a save made by this game version to check it."
            End If
            Log(res.Note)
            Return res
        End Function

        ''' <summary>Finds "azeroth-survivors/save/..." stored xor 0x36 or 0x5C (a precomputed HMAC key block) and decodes it.</summary>
        Private Function PaddedKeys() As List(Of (Prefix As String, Secret As Byte(), Off As Long))
            Dim res As New List(Of (Prefix As String, Secret As Byte(), Off As Long))
            Dim plain = Encoding.ASCII.GetBytes("azeroth-survivors/save/")
            For Each pad In {CByte(&H36), CByte(&H5C)}
                Dim pat = plain.Select(Function(c) CByte(c Xor pad)).ToArray()
                Dim i = 0
                While True
                    Dim rel = Data.AsSpan(i).IndexOf(pat)
                    If rel < 0 Then Exit While
                    i += rel
                    Dim st = i
                    Dim blk = Data.Skip(st).Take(64).Select(Function(c) CByte(c Xor pad)).ToArray()
                    ' prefix = text up to the last "/" ; secret = following bytes up to a run of zeros
                    Dim pe = plain.Length
                    While pe < blk.Length AndAlso blk(pe) >= &H21 AndAlso blk(pe) < &H7F AndAlso blk(pe) <> CByte(AscW("/"c)) : pe += 1 : End While
                    If pe < blk.Length AndAlso blk(pe) = CByte(AscW("/"c)) Then pe += 1
                    Dim se = pe
                    While se < blk.Length AndAlso Not (se + 3 < blk.Length AndAlso blk(se) = 0 AndAlso blk(se + 1) = 0 AndAlso blk(se + 2) = 0 AndAlso blk(se + 3) = 0) : se += 1 : End While
                    If se - pe >= 8 Then
                        Dim pre = Encoding.ASCII.GetString(blk, 0, pe)
                        Dim sec = blk.Skip(pe).Take(se - pe).ToArray()
                        If Not res.Any(Function(r) r.Prefix = pre AndAlso r.Secret.SequenceEqual(sec)) Then res.Add((pre, sec, st))
                    End If
                    i = st + 1
                End While
            Next
            Return res
        End Function

        ' ======================= dump =======================

        Public Sub DumpStrings(path As String)
            Using w As New StreamWriter(path, False, New UTF8Encoding(False))
                For Each f In Fields
                    w.Write(f.Offset.ToString("X8")) : w.Write(vbTab) : w.WriteLine(f.Text.Replace(vbCr, " ").Replace(vbLf, " "))
                Next
            End Using
        End Sub
    End Class

End Namespace
