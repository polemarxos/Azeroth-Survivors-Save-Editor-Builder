Imports System.IO
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Windows exe support. The game is a compiled (Rust) program: its texts sit back to back in the .rdata section
    ''' with no separators, so "strings" alone can't tell where one ends. The exact strings are recovered from the
    ''' places that use them: (pointer, length) pairs in the data sections, and "lea reg,[rip+x]" + "mov ..., length"
    ''' in the code. Data pairs also reveal the game's data tables (structs), whose numbers (max rank, row, column...)
    ''' are read straight from the struct bytes.
    ''' </summary>
    Partial Public Class ExeScanner

        Private PeMode As Boolean
        Private ImageBase As Long
        Private ReadOnly Secs As New List(Of (Name As String, Va As Long, VSize As Long, Raw As Long, RSize As Long))
        ''' <summary>string file offset -> length, for every string something in the program points at.</summary>
        Private ReadOnly RefLen As New Dictionary(Of Integer, Integer)
        ''' <summary>string file offset -> file offsets of (pointer, length) pairs that point at it.</summary>
        Private ReadOnly PairsTo As New Dictionary(Of Integer, List(Of Integer))
        Private rdLo, rdHi As Long

        Private Function OffToVa(o As Long) As Long
            For Each s In Secs
                If o >= s.Raw AndAlso o < s.Raw + s.RSize Then Return ImageBase + s.Va + (o - s.Raw)
            Next
            Return -1
        End Function

        Private Function VaToOff(v As Long) As Long
            Dim r = v - ImageBase
            For Each s In Secs
                If r >= s.Va AndAlso r < s.Va + s.RSize Then Return s.Raw + (r - s.Va)
            Next
            Return -1
        End Function

        Private Function ParsePe() As Boolean
            Try
                If Data.Length < 1024 OrElse Data(0) <> &H4D OrElse Data(1) <> &H5A Then Return False
                Dim pe = BitConverter.ToInt32(Data, &H3C)
                If pe <= 0 OrElse pe > Data.Length - 256 OrElse BitConverter.ToInt32(Data, pe) <> &H4550 Then Return False
                Dim nSec = BitConverter.ToUInt16(Data, pe + 6)
                Dim optSize = BitConverter.ToUInt16(Data, pe + 20)
                Dim opt = pe + 24
                Dim magic = BitConverter.ToUInt16(Data, opt)
                ImageBase = If(magic = &H20B, BitConverter.ToInt64(Data, opt + 24), BitConverter.ToUInt32(Data, opt + 28))
                Dim sec = opt + optSize
                For i = 0 To nSec - 1
                    Dim s = sec + i * 40
                    Dim nm = Encoding.ASCII.GetString(Data, s, 8).TrimEnd(ChrW(0))
                    Secs.Add((nm, BitConverter.ToUInt32(Data, s + 12), BitConverter.ToUInt32(Data, s + 8), BitConverter.ToUInt32(Data, s + 20), BitConverter.ToUInt32(Data, s + 16)))
                Next
                Return Secs.Any(Function(x) x.Name = ".rdata") AndAlso Secs.Any(Function(x) x.Name = ".text")
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Valid UTF-8 text without control characters (tabs and new lines allowed).</summary>
        Private Function Printable(o As Long, len As Integer) As Boolean
            If o < 0 OrElse len <= 0 OrElse o + len > Data.LongLength Then Return False
            Dim i = CInt(o), e = CInt(o) + len
            While i < e
                Dim c = Data(i)
                If c < &H20 Then
                    If c <> 9 AndAlso c <> 10 AndAlso c <> 13 Then Return False
                    i += 1
                ElseIf c < &H7F Then
                    i += 1
                ElseIf c = &H7F Then
                    Return False
                Else
                    Dim l = Utf8Len(Data, i)
                    If l = 0 OrElse i + l > e Then Return False
                    i += l
                End If
            End While
            Return True
        End Function

        ''' <summary>Builds the exact string list of a Windows exe. Returns False if the file is not one (or has too few).</summary>
        Private Function TryLoadPe() As Boolean
            If Not ParsePe() Then Return False
            Dim rd = Secs.First(Function(x) x.Name = ".rdata")
            rdLo = ImageBase + rd.Va : rdHi = rdLo + Math.Max(rd.VSize, rd.RSize)
            For Each s In Secs.Where(Function(x) x.Name = ".rdata" OrElse x.Name = ".data")
                ScanPairs(s.Raw, s.RSize)
            Next
            Dim tx = Secs.First(Function(x) x.Name = ".text")
            ScanLeas(tx.Raw, tx.RSize, tx.Va)
            CodeStr.Sort(Function(a, c) a.Code.CompareTo(c.Code))
            ScanInline(tx.Raw, tx.RSize)
            ScanTemplates()
            If RefLen.Count < 2000 Then Return False

            For Each kv In RefLen
                Dim t = Encoding.UTF8.GetString(Data, kv.Key, kv.Value)
                Fields.Add(New Field(kv.Key, t))
            Next
            Fields.Sort(Function(a, b) a.Offset.CompareTo(b.Offset))
            For Each f In Fields
                If f.Text.Length > 200 Then Continue For
                For Each m As Text.RegularExpressions.Match In WordRx.Matches(f.Text)
                    If m.Value.Length > 60 Then Continue For
                    Dim lst As List(Of (Off As Long, Alone As Boolean)) = Nothing
                    If Not Words.TryGetValue(m.Value, lst) Then lst = New List(Of (Off As Long, Alone As Boolean)) : Words(m.Value) = lst
                    If lst.Count < 400 Then lst.Add((f.Offset + m.Index, f.Text = m.Value))
                Next
                If f.Text.Length >= 32 Then
                    For Each m As Text.RegularExpressions.Match In HexRx.Matches(f.Text)
                        HexCands.Add((f.Offset + m.Index, m.Value.ToLowerInvariant()))
                    Next
                End If
            Next
            PeMode = True
            Return True
        End Function

        Private Sub ScanPairs(raw As Long, size As Long)
            Dim e = raw + size - 16
            Dim i = raw + ((8 - raw Mod 8) Mod 8)
            While i <= e
                Dim p = BitConverter.ToInt64(Data, CInt(i))
                If p >= rdLo AndAlso p < rdHi Then
                    Dim l = BitConverter.ToInt64(Data, CInt(i) + 8)
                    If l >= 1 AndAlso l <= 600 Then
                        Dim o = VaToOff(p)
                        If Printable(o, CInt(l)) Then
                            RefLen.TryAdd(CInt(o), CInt(l))
                            Dim lst As List(Of Integer) = Nothing
                            If Not PairsTo.TryGetValue(CInt(o), lst) Then lst = New List(Of Integer) : PairsTo(CInt(o)) = lst
                            If lst.Count < 50 Then lst.Add(CInt(i))
                        End If
                    End If
                End If
                i += 8
            End While
        End Sub

        ''' <summary>lea r64,[rip+disp32] to a string, with its length in a mov a few instructions later.</summary>
        Private Sub ScanLeas(raw As Long, size As Long, va As Long)
            Dim b = Data
            Dim e = CInt(Math.Min(raw + size, b.Length) - 48)
            For j = CInt(raw) To e
                Dim r = b(j)
                If (r <> &H48 AndAlso r <> &H4C) OrElse b(j + 1) <> &H8D OrElse (b(j + 2) And &HC7) <> 5 Then Continue For
                Dim nxt = j + 7
                Dim tgt = ImageBase + va + (nxt - raw) + BitConverter.ToInt32(b, j + 3)
                If tgt < rdLo OrElse tgt >= rdHi Then Continue For
                Dim o = VaToOff(tgt)
                If o < 0 Then Continue For
                If RefLen.ContainsKey(CInt(o)) Then CodeStr.Add((j, CInt(o))) : Continue For
                For k = nxt To nxt + 36
                    Dim imm = ImmAt(b, k)
                    If imm >= 1 AndAlso imm <= 600 AndAlso Printable(o, imm) Then
                        RefLen(CInt(o)) = imm
                        CodeStr.Add((j, CInt(o)))
                        Exit For
                    End If
                Next
            Next
        End Sub

        ''' <summary>Code places that load a string (code offset, string offset), in code order.</summary>
        Private ReadOnly CodeStr As New List(Of (Code As Integer, Str As Integer))
        ''' <summary>Ids the code builds byte by byte (e.g. "codex:50" written with mov instructions), with their code offset.</summary>
        Private ReadOnly InlineIds As New List(Of (Code As Integer, Id As String))
        Private ReadOnly InlineSet As New HashSet(Of String)
        ''' <summary>Id prefixes the game fills in at run time from a format string ("zone9:" + zone).</summary>
        Private ReadOnly TemplatePrefixes As New HashSet(Of String)

        Private Shared ReadOnly IdRx As New Text.RegularExpressions.Regex("^[a-z][a-z0-9_]*(:[a-z0-9_]+)*$", Text.RegularExpressions.RegexOptions.Compiled)

        Private Shared Function IsAsciiPrint(b As Byte(), o As Integer, n As Integer) As Boolean
            For i = o To o + n - 1
                If b(i) < &H20 OrElse b(i) > &H7E Then Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' Finds strings the code writes into memory piece by piece: "movabs rax, 'count:ma'" + "mov [rcx], rax"
        ''' + "mov [rcx+6], ..." and so on. A tiny emulator replays the stores around each printable movabs.
        ''' </summary>
        Private Sub ScanInline(raw As Long, size As Long)
            Dim b = Data
            Dim st = CInt(raw) + 48, e = CInt(Math.Min(raw + size, b.Length) - 80)
            Dim lastCode = -1
            For j = st To e
                Dim r = b(j)
                If (r <> &H48 AndAlso r <> &H49) OrElse b(j + 1) < &HB8 OrElse b(j + 1) > &HBF Then Continue For
                If Not IsAsciiPrint(b, j + 2, 8) Then Continue For
                Dim piece = Encoding.ASCII.GetString(b, j + 2, 8)
                Dim s = EmulateStores(b, j - 40, j + 60)
                If s Is Nothing OrElse Not s.Contains(piece) OrElse Not IdRx.IsMatch(s) Then Continue For
                If j = lastCode Then Continue For
                lastCode = j
                InlineIds.Add((j, s))
                InlineSet.Add(s)
            Next
        End Sub

        Private Shared Function EmulateStores(b As Byte(), fromK As Integer, toK As Integer) As String
            Dim regs As New Dictionary(Of Integer, Byte())
            Dim bufs As New Dictionary(Of Integer, Dictionary(Of Integer, Byte))
            Dim put = Sub(rm As Integer, disp As Integer, src As Byte(), n As Integer)
                          Dim bf As Dictionary(Of Integer, Byte) = Nothing
                          If Not bufs.TryGetValue(rm, bf) Then bf = New Dictionary(Of Integer, Byte) : bufs(rm) = bf
                          For i = 0 To n - 1 : bf(disp + i) = src(i) : Next
                      End Sub
            Dim k = fromK
            While k < toK
                Dim rex = 0, p = k
                Dim c0 = b(k)
                If c0 = &H40 OrElse c0 = &H41 OrElse c0 = &H44 OrElse c0 = &H45 OrElse c0 = &H48 OrElse c0 = &H49 OrElse c0 = &H4C OrElse c0 = &H4D Then rex = c0 : p = k + 1
                Dim op = b(p)
                If (rex And 8) <> 0 AndAlso op >= &HB8 AndAlso op <= &HBF Then
                    regs((op - &HB8) + If((rex And 1) <> 0, 8, 0)) = b.Skip(p + 1).Take(8).ToArray()
                    k = p + 9 : Continue While
                End If
                If op = &H89 AndAlso (rex And 8) <> 0 Then
                    Dim mrm = b(p + 1), md = mrm >> 6
                    Dim rg = ((mrm >> 3) And 7) + If((rex And 4) <> 0, 8, 0), rm = (mrm And 7) + If((rex And 1) <> 0, 8, 0)
                    If md = 1 AndAlso (mrm And 7) <> 4 Then
                        If regs.ContainsKey(rg) Then put(rm, CSByte(If(b(p + 2) > 127, CInt(b(p + 2)) - 256, CInt(b(p + 2)))), regs(rg), 8)
                        k = p + 3 : Continue While
                    End If
                    If md = 0 AndAlso (mrm And 7) <> 4 AndAlso (mrm And 7) <> 5 Then
                        If regs.ContainsKey(rg) Then put(rm, 0, regs(rg), 8)
                        k = p + 2 : Continue While
                    End If
                End If
                If c0 = &H66 Then
                    Dim q = k + 1, rx = 0
                    If b(q) = &H41 Then rx = &H41 : q += 1
                    If b(q) = &HC7 Then
                        Dim mrm = b(q + 1)
                        If (mrm >> 6) = 1 AndAlso ((mrm >> 3) And 7) = 0 Then
                            Dim rm = (mrm And 7) + If((rx And 1) <> 0, 8, 0)
                            put(rm, If(b(q + 2) > 127, CInt(b(q + 2)) - 256, CInt(b(q + 2))), {b(q + 3), b(q + 4)}, 2)
                            k = q + 5 : Continue While
                        End If
                    End If
                End If
                If (op = &HC7 OrElse op = &HC6) AndAlso (rex And 8) = 0 Then
                    Dim mrm = b(p + 1), md = mrm >> 6, n = If(op = &HC7, 4, 1)
                    Dim rm = (mrm And 7) + If((rex And 1) <> 0, 8, 0)
                    If ((mrm >> 3) And 7) = 0 AndAlso (mrm And 7) <> 4 Then
                        If md = 1 Then
                            put(rm, If(b(p + 2) > 127, CInt(b(p + 2)) - 256, CInt(b(p + 2))), b.Skip(p + 3).Take(n).ToArray(), n)
                            k = p + 3 + n : Continue While
                        End If
                        If md = 0 AndAlso (mrm And 7) <> 5 Then
                            put(rm, 0, b.Skip(p + 2).Take(n).ToArray(), n)
                            k = p + 2 + n : Continue While
                        End If
                    End If
                End If
                k += 1
            End While
            Dim best As String = Nothing
            For Each bf In bufs.Values
                Dim sb As New StringBuilder()
                Dim i = 0
                Dim v As Byte
                While bf.TryGetValue(i, v)
                    If v < &H20 OrElse v > &H7E Then Exit While
                    sb.Append(ChrW(v)) : i += 1
                End While
                If sb.Length >= 4 AndAlso (best Is Nothing OrElse sb.Length > best.Length) Then best = sb.ToString()
            Next
            Return best
        End Function

        ''' <summary>Format strings are stored as pieces: [length][text][0xC0 0x00 = value goes here]. Collect "prefix:" pieces.</summary>
        Private Sub ScanTemplates()
            Dim rd = Secs.First(Function(x) x.Name = ".rdata")
            Dim b = Data
            Dim e = CInt(Math.Min(rd.Raw + rd.RSize, b.Length) - 2)
            For i = CInt(rd.Raw) + 34 To e
                If b(i) <> &HC0 OrElse b(i + 1) <> 0 OrElse b(i - 1) <> &H3A Then Continue For   ' ...":" C0 00
                For L = 2 To 31
                    If b(i - L - 1) = L AndAlso IsAsciiPrint(b, i - L, L) Then
                        Dim s = Encoding.ASCII.GetString(b, i - L, L)
                        If IdRx.IsMatch(s.TrimEnd(":"c)) Then TemplatePrefixes.Add(s)
                        Exit For
                    End If
                Next
            Next
        End Sub

        ''' <summary>32-bit immediate of a "mov reg/[mem], imm32" starting at k, or -1.</summary>
        Private Shared Function ImmAt(b As Byte(), k As Integer) As Integer
            Dim c = b(k)
            If c = &H48 OrElse c = &H49 Then
                If b(k + 1) <> &HC7 Then Return -1
                Dim m = b(k + 2)
                If m = &H44 AndAlso b(k + 3) = &H24 Then Return BitConverter.ToInt32(b, k + 5)   ' [rsp+d8]
                If m = &H84 AndAlso b(k + 3) = &H24 Then Return BitConverter.ToInt32(b, k + 8)   ' [rsp+d32]
                If m >= &H40 AndAlso m <= &H47 Then Return BitConverter.ToInt32(b, k + 4)         ' [reg+d8]
                If m >= &H80 AndAlso m <= &H87 Then Return BitConverter.ToInt32(b, k + 7)         ' [reg+d32]
                If m >= &HC0 AndAlso m <= &HC7 Then Return BitConverter.ToInt32(b, k + 3)         ' reg
                Return -1
            End If
            If c = &H41 AndAlso b(k + 1) >= &HB8 AndAlso b(k + 1) <= &HBF Then Return BitConverter.ToInt32(b, k + 2)
            If c >= &HB8 AndAlso c <= &HBF Then Return BitConverter.ToInt32(b, k + 1)
            Return -1
        End Function

        ' ---------- struct data (tables of records with (pointer, length) string fields) ----------

        ''' <summary>The (pointer, length) pair of an id string that sits in (or nearest to) the table's records.</summary>
        Private Function PairOf(stringOff As Long, spans As List(Of (Lo As Long, Hi As Long))) As Integer
            Dim l As List(Of Integer) = Nothing
            If Not PairsTo.TryGetValue(CInt(stringOff), l) Then Return -1
            If spans Is Nothing OrElse spans.Count = 0 Then Return -1
            Dim best = -1, bd = Long.MaxValue
            For Each p In l
                For Each nr In spans
                    Dim d = If(p < nr.Lo, nr.Lo - p, If(p > nr.Hi, p - nr.Hi, 0))
                    If d < bd Then bd = d : best = p
                Next
            Next
            Return If(bd <= 8192, best, -1)
        End Function

        ''' <summary>Where the records of a table sit: runs of (pointer, length) pairs for the known ids.</summary>
        Private Function PairRegion(ids As IEnumerable(Of String)) As List(Of (Lo As Long, Hi As Long))
            Dim pts As New List(Of (P As Long, Id As String))
            For Each id In ids
                Dim l = Occ(id)
                If l Is Nothing Then Continue For
                For Each o In l
                    If Not o.Alone Then Continue For
                    Dim pl As List(Of Integer) = Nothing
                    If PairsTo.TryGetValue(CInt(o.Off), pl) Then pts.AddRange(pl.Select(Function(x) (CLng(x), id)))
                Next
            Next
            Dim res As New List(Of (Lo As Long, Hi As Long))
            If pts.Count < 3 Then Return res
            pts.Sort(Function(a, b) a.P.CompareTo(b.P))
            Dim st = 0
            Dim runs As New List(Of (Lo As Long, Hi As Long, N As Integer))
            For i = 1 To pts.Count
                If i = pts.Count OrElse pts(i).P - pts(i - 1).P > 4096 Then
                    Dim n = pts.Skip(st).Take(i - st).Select(Function(x) x.Id).Distinct().Count()
                    runs.Add((pts(st).P, pts(i - 1).P, n))
                    st = i
                End If
            Next
            ' a run is a table part when it holds several known ids; a run of only 1-2 is a stray reference
            Dim maxN = runs.Max(Function(x) x.N)
            For Each r In runs
                If r.N >= 3 OrElse r.N = maxN Then res.Add((r.Lo, r.Hi))
            Next
            ' the same id usually has a pair in several unrelated places: keep the runs with the most ids
            Return res.OrderByDescending(Function(x) runs.First(Function(y) y.Lo = x.Lo).N).Take(12).ToList()
        End Function

        ''' <summary>
        ''' The record around a pair, as values keyed by their place: "s+24" = string at +24 bytes,
        ''' "b+40" = byte, "i+40" = 32-bit integer, "f+40" = whole-number float.
        ''' </summary>
        Private Sub AddStructTokens(toks As List(Of Tok), pair As Integer)
            If pair < 0 Then Return
            For d = -192 To 192
                Dim p = pair + d
                If p < 0 OrElse p + 16 > Data.Length Then Continue For
                If d Mod 8 = 0 AndAlso d <> 0 Then
                    Dim v = BitConverter.ToInt64(Data, p)
                    If v >= rdLo AndAlso v < rdHi Then
                        Dim l = BitConverter.ToInt64(Data, p + 8)
                        Dim o = VaToOff(v)
                        If l >= 1 AndAlso l <= 600 AndAlso Printable(o, CInt(l)) Then
                            toks.Add(New Tok With {.Key = "s" & d.ToString("+0;-0"), .Value = Encoding.UTF8.GetString(Data, CInt(o), CInt(l))})
                        End If
                    End If
                End If
                If d >= 0 AndAlso d < 16 Then Continue For   ' the id's own pointer and length
                toks.Add(New Tok With {.Key = "b" & d.ToString("+0;-0"), .Value = Data(p).ToString()})
                If d Mod 4 = 0 Then
                    toks.Add(New Tok With {.Key = "i" & d.ToString("+0;-0"), .Value = BitConverter.ToInt32(Data, p).ToString()})
                    Dim f = BitConverter.ToSingle(Data, p)
                    If Single.IsFinite(f) AndAlso f = Math.Floor(f) AndAlso Math.Abs(f) < 100000 Then
                        toks.Add(New Tok With {.Key = "f" & d.ToString("+0;-0"), .Value = CInt(f).ToString()})
                    End If
                End If
            Next
        End Sub

        ''' <summary>Strings just before and after an id in the file (keys "p-2", "p+1"...), for compiled programs.</summary>
        Private Sub AddNeighbourTokens(toks As List(Of Tok), off As Long, id As String)
            Dim hi = FirstFieldAfter(off)
            Dim lo = hi - 1
            If lo >= 0 AndAlso Fields(lo).Offset = off Then lo -= 1
            For k = 1 To 8
                If hi + k - 1 < Fields.Count AndAlso Fields(hi + k - 1).Offset - off < 1500 Then toks.Add(New Tok With {.Key = "p+" & k, .Value = Fields(hi + k - 1).Text})
            Next
            For k = 1 To 4
                If lo - k + 1 >= 0 AndAlso off - Fields(lo - k + 1).Offset < 1500 Then toks.Add(New Tok With {.Key = "p-" & k, .Value = Fields(lo - k + 1).Text})
            Next
        End Sub
        ''' <summary>The table's record area, when (nearly) all known ids are stored as records; Nothing otherwise.</summary>
        Private Function RecordSpans(ids As ICollection(Of String)) As List(Of (Lo As Long, Hi As Long))
            Dim spans = PairRegion(ids)
            If spans.Count = 0 Then Return Nothing
            Dim withRec = 0, total = 0
            For Each id In ids
                Dim l = Occ(id)
                If l Is Nothing Then Continue For
                total += 1
                If l.Any(Function(o) o.Alone AndAlso InRecords(o.Off, spans)) Then withRec += 1
            Next
            Return If(total > 0 AndAlso withRec >= total * 0.8, spans, Nothing)
        End Function

        ''' <summary>Is a string pointed at from inside the record area?</summary>
        Private Function InRecords(stringOff As Long, spans As List(Of (Lo As Long, Hi As Long))) As Boolean
            Dim l As List(Of Integer) = Nothing
            If Not PairsTo.TryGetValue(CInt(stringOff), l) Then Return False
            For Each p In l
                For Each sp In spans
                    If p >= sp.Lo - 512 AndAlso p <= sp.Hi + 512 Then Return True
                Next
            Next
            Return False
        End Function

        ' ---------- tables keyed by a number (tabards) ----------

        ''' <summary>
        ''' Tabards are records with a name string and a numeric id (90001...). The record layout is learnt from the
        ''' tabards the editor knows (where the id sits next to the name), then every record of the table is read.
        ''' </summary>
        Private Function StructTabards(known As KnownData) As List(Of Finding)
            Dim out As New List(Of Finding)
            If Not PeMode Then Return out
            Dim tabIds = known.IdsOf(Cat.Tabard)
            Dim suffix = " Tabard"
            Dim samples As New List(Of (Pair As Integer, Id As Integer))
            For Each tabKey In tabIds
                Dim id As Integer
                If Not Integer.TryParse(tabKey, id) Then Continue For
                Dim nm = known.GetProp(Cat.Tabard, tabKey, "name")
                If nm Is Nothing Then Continue For
                For Each txt In {nm & suffix, nm}
                    For Each fi In FieldsWithText(txt)
                        Dim pl As List(Of Integer) = Nothing
                        If PairsTo.TryGetValue(CInt(Fields(fi).Offset), pl) Then
                            For Each p In pl : samples.Add((p, id)) : Next
                        End If
                    Next
                Next
            Next
            If samples.Count < 3 Then Return out
            Dim hits As New Dictionary(Of Integer, Integer)
            For Each smp In samples
                For d = -128 To 128 Step 4
                    Dim p = smp.Pair + d
                    If p < 0 OrElse p + 4 > Data.Length Then Continue For
                    If BitConverter.ToInt32(Data, p) = smp.Id Then hits(d) = hits.GetValueOrDefault(d) + 1
                Next
            Next
            If hits.Count = 0 Then Return out
            Dim idAt = hits.OrderByDescending(Function(x) x.Value).First()
            Dim good = samples.Where(Function(x) BitConverter.ToInt32(Data, x.Pair + idAt.Key) = x.Id).Select(Function(x) x.Pair).Distinct().OrderBy(Function(x) x).ToList()
            If good.Count < 3 Then Return out
            Dim diffs As New List(Of Integer)
            For i = 1 To good.Count - 1
                Dim df = good(i) - good(i - 1)
                If df > 0 AndAlso df <= 512 Then diffs.Add(df)
            Next
            If diffs.Count = 0 Then Return out
            Dim stride = diffs.GroupBy(Function(x) x).OrderByDescending(Function(g) g.Count()).First().Key
            Log($"Tabards: records of {stride} bytes, id {idAt.Key:+0;-0} bytes from the name ({good.Count} known records).")

            Dim readRec = Function(p As Integer) As (Ok As Boolean, Id As Integer, Name As String)
                              If p < 0 OrElse p + 16 > Data.Length OrElse p + idAt.Key < 0 Then Return (False, 0, Nothing)
                              Dim v = BitConverter.ToInt64(Data, p), l = BitConverter.ToInt64(Data, p + 8)
                              If v < rdLo OrElse v >= rdHi OrElse l < 1 OrElse l > 80 Then Return (False, 0, Nothing)
                              Dim o = VaToOff(v)
                              If Not Printable(o, CInt(l)) Then Return (False, 0, Nothing)
                              Dim id = BitConverter.ToInt32(Data, p + idAt.Key)
                              If id < 90000 OrElse id > 99999 Then Return (False, 0, Nothing)
                              Return (True, id, Encoding.UTF8.GetString(Data, CInt(o), CInt(l)))
                          End Function
            Dim recs As New Dictionary(Of Integer, String)
            For Each anchor In good
                For sgn = -1 To 1 Step 2
                    For k = If(sgn = 1, 0, 1) To 200
                        Dim r = readRec(anchor + sgn * k * stride)
                        If Not r.Ok Then Exit For
                        recs.TryAdd(r.Id, r.Name)
                    Next
                Next
            Next
            For Each kv In recs.OrderBy(Function(x) x.Key)
                Dim nm = kv.Value
                If nm.EndsWith(suffix) Then nm = nm.Substring(0, nm.Length - suffix.Length)
                ' the editor shows "Tabard of the Horde" as "Horde"
                For Each pfx In {"Tabard of the ", "Tabard of "}
                    If nm.StartsWith(pfx) Then nm = nm.Substring(pfx.Length) : Exit For
                Next
                Dim tabKey = kv.Key.ToString()
                If Not tabIds.Contains(tabKey) Then
                    If kv.Key >= 90100 Then Continue For    ' 901xx are cosmetics, not tabards
                    out.Add(New Finding With {.Category = Cat.Tabard, .Id = tabKey, .Name = nm, .Icon = "INV_Shirt_GuildTabard_01", .Offset = -1})
                ElseIf known.GetProp(Cat.Tabard, tabKey, "name") <> nm Then
                    out.Add(New Finding With {.Category = Cat.Tabard, .Status = FindStatus.Changed, .Prop = "name", .Id = tabKey, .Name = nm,
                        .Notes = "name was: " & known.GetProp(Cat.Tabard, tabKey, "name")})
                End If
            Next
            Dim missing = tabIds.Where(Function(x) Not recs.ContainsKey(CInt(x))).ToList()
            If recs.Count >= tabIds.Count * 0.9 Then
                For Each m In missing
                    out.Add(New Finding With {.Category = Cat.Tabard, .Status = FindStatus.Missing, .Id = m, .Include = False,
                        .Name = known.GetProp(Cat.Tabard, m, "name"), .Notes = "in the editor but not in this game file"})
                Next
            End If
            Log($"Tabards: {recs.Count} in the game, {out.Where(Function(f) f.Status = FindStatus.New).Count()} new.")
            Return out
        End Function
    End Class


End Namespace
