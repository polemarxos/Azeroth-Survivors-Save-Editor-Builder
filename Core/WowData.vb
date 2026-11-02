Imports System.IO
Imports System.IO.Compression
Imports System.Text

Namespace Core

    ''' <summary>
    ''' Reads item icons from a World of Warcraft 1.12 client, the same data the game itself loads (mpq://...).
    ''' ItemDisplayInfo.dbc (inside Data\*.MPQ) maps a display id to its inventory icon name.
    ''' </summary>
    Public Class WowData

        ''' <summary>display id -> icon name (e.g. 35420 -> "INV_Helmet_58").</summary>
        Public ReadOnly Icons As New Dictionary(Of Integer, String)
        Public Property Source As String = ""

        ''' <summary>Finds the WoW Data folder from a folder the user picked (or the game's own folder).</summary>
        Public Shared Function FindDataFolder(start As String) As String
            If String.IsNullOrWhiteSpace(start) Then Return Nothing
            Dim d = If(File.Exists(start), Path.GetDirectoryName(start), start)
            For i = 0 To 3
                If d Is Nothing Then Exit For
                For Each cand In {d, Path.Combine(d, "Data")}
                    If Directory.Exists(cand) AndAlso Directory.GetFiles(cand, "*.mpq").Length + Directory.GetFiles(cand, "*.MPQ").Length > 0 Then Return cand
                Next
                Dim dbc = Path.Combine(d, "ItemDisplayInfo.dbc")
                If File.Exists(dbc) Then Return d
                d = Path.GetDirectoryName(d)
            Next
            Return Nothing
        End Function

        ''' <summary>Loads ItemDisplayInfo from a loose .dbc file or from the MPQ archives (patches first).</summary>
        Public Shared Function Load(folder As String, Optional log As Action(Of String) = Nothing) As WowData
            Dim w As New WowData()
            Dim dataDir = FindDataFolder(folder)
            If dataDir Is Nothing Then Return Nothing
            Dim loose = Directory.GetFiles(dataDir, "ItemDisplayInfo.dbc", SearchOption.AllDirectories).FirstOrDefault()
            Dim bytes As Byte() = Nothing
            If loose IsNot Nothing Then
                bytes = File.ReadAllBytes(loose) : w.Source = loose
            Else
                ' later patches override earlier archives
                Dim mpqs = Directory.GetFiles(dataDir).Where(Function(f) f.EndsWith(".mpq", StringComparison.OrdinalIgnoreCase)).
                           OrderByDescending(Function(f) ArchivePriority(Path.GetFileName(f))).ToList()
                For Each m In mpqs
                    Try
                        Using a As New MpqArchive(m)
                            bytes = a.ReadFile("DBFilesClient\ItemDisplayInfo.dbc")
                        End Using
                    Catch ex As Exception
                        log?.Invoke("  " & Path.GetFileName(m) & ": " & ex.Message)
                        bytes = Nothing
                    End Try
                    If bytes IsNot Nothing Then w.Source = m : Exit For
                Next
            End If
            If bytes Is Nothing Then Return Nothing
            w.ReadDbc(bytes)
            Return w
        End Function

        Private Shared Function ArchivePriority(name As String) As Integer
            Dim n = name.ToLowerInvariant()
            If n.StartsWith("patch-") Then
                Dim c = n.Substring(6, 1)
                Return 1000 + If(Char.IsDigit(c(0)), CInt(c) * 10, AscW(c(0)))
            End If
            If n.StartsWith("patch") Then Return 900
            If n = "dbc.mpq" Then Return 500
            Return 100
        End Function

        ''' <summary>WDBC: header, fixed-size records, string block. The icon column is found by its content.</summary>
        Private Sub ReadDbc(b As Byte())
            If b.Length < 20 OrElse Encoding.ASCII.GetString(b, 0, 4) <> "WDBC" Then Throw New InvalidDataException("not a DBC file")
            Dim recs = BitConverter.ToInt32(b, 4), fields = BitConverter.ToInt32(b, 8), size = BitConverter.ToInt32(b, 12), strSize = BitConverter.ToInt32(b, 16)
            Dim strBase = 20 + recs * size
            Dim getStr = Function(o As Integer) As String
                             If o <= 0 OrElse o >= strSize Then Return Nothing
                             Dim e = Array.IndexOf(b, CByte(0), strBase + o)
                             If e < 0 Then Return Nothing
                             Return Encoding.UTF8.GetString(b, strBase + o, e - strBase - o)
                         End Function
            ' which column holds icon names?
            Dim best = -1, bestHits = 0
            For f = 1 To Math.Min(fields, size \ 4) - 1
                Dim hits = 0
                For r = 0 To Math.Min(recs, 400) - 1
                    Dim s = getStr(BitConverter.ToInt32(b, 20 + r * size + f * 4))
                    If s IsNot Nothing AndAlso (s.StartsWith("INV_", StringComparison.OrdinalIgnoreCase) OrElse s.StartsWith("Spell_", StringComparison.OrdinalIgnoreCase)) Then hits += 1
                Next
                If hits > bestHits Then bestHits = hits : best = f
            Next
            If best < 0 Then Throw New InvalidDataException("no icon column in ItemDisplayInfo.dbc")
            For r = 0 To recs - 1
                Dim id = BitConverter.ToInt32(b, 20 + r * size)
                Dim s = getStr(BitConverter.ToInt32(b, 20 + r * size + best * 4))
                If Not String.IsNullOrEmpty(s) Then Icons(id) = Path.GetFileNameWithoutExtension(s)
            Next
        End Sub
    End Class

    ''' <summary>Minimal reader for MPQ archives (format 1, as used by WoW 1.12): zlib and PKWARE compression.</summary>
    Public Class MpqArchive
        Implements IDisposable

        Private ReadOnly fs As FileStream
        Private ReadOnly baseOff As Long
        Private ReadOnly sectorSize As Integer
        Private ReadOnly hashTable As UInteger()
        Private ReadOnly blockTable As UInteger()

        Private Shared ReadOnly Crypt As UInteger() = BuildCrypt()

        Private Shared Function BuildCrypt() As UInteger()
            Dim t(&H500 - 1) As UInteger
            Dim seed As UInteger = &H100001UI
            For i = 0 To 255
                Dim idx = i
                For j = 0 To 4
                    seed = CUInt((CULng(seed) * 125UL + 3UL) Mod &H2AAAABUL)
                    Dim t1 = (seed And &HFFFFUI) << 16
                    seed = CUInt((CULng(seed) * 125UL + 3UL) Mod &H2AAAABUL)
                    Dim t2 = seed And &HFFFFUI
                    t(idx) = t1 Or t2
                    idx += 256
                Next
            Next
            Return t
        End Function

        Public Shared Function HashString(s As String, type As Integer) As UInteger
            Dim seed1 As UInteger = &H7FED7FEDUI, seed2 As UInteger = &HEEEEEEEEUI
            For Each ch In s.ToUpperInvariant()
                Dim c = CUInt(AscW(ch)) And &HFFUI
                seed1 = Crypt(type * 256 + CInt(c)) Xor (seed1 + seed2)
                seed2 = c + seed1 + seed2 + (seed2 << 5) + 3UI
            Next
            Return seed1
        End Function

        Private Shared Sub Decrypt(data As UInteger(), key As UInteger)
            Dim seed2 As UInteger = &HEEEEEEEEUI
            For i = 0 To data.Length - 1
                seed2 += Crypt(&H400 + CInt(key And &HFFUI))
                Dim ch = data(i) Xor (key + seed2)
                key = (((Not key) << &H15) + &H11111111UI) Or (key >> &HB)
                seed2 = ch + seed2 + (seed2 << 5) + 3UI
                data(i) = ch
            Next
        End Sub

        Private Shared Sub DecryptBytes(b As Byte(), key As UInteger)
            Dim n = b.Length \ 4
            Dim u(n - 1) As UInteger
            Buffer.BlockCopy(b, 0, u, 0, n * 4)
            Decrypt(u, key)
            Buffer.BlockCopy(u, 0, b, 0, n * 4)
        End Sub

        Public Sub New(path As String)
            fs = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            Dim hdr(31) As Byte
            baseOff = -1
            For off As Long = 0 To Math.Min(fs.Length - 32, 1024L * 1024L) Step 512
                fs.Position = off
                fs.ReadExactly(hdr, 0, 32)
                If hdr(0) = &H4D AndAlso hdr(1) = &H50 AndAlso hdr(2) = &H51 AndAlso hdr(3) = &H1A Then baseOff = off : Exit For
            Next
            If baseOff < 0 Then Throw New InvalidDataException("not an MPQ archive")
            sectorSize = 512 << BitConverter.ToUInt16(hdr, 14)
            Dim hashPos = BitConverter.ToUInt32(hdr, 16), blockPos = BitConverter.ToUInt32(hdr, 20)
            Dim hashCount = CInt(BitConverter.ToUInt32(hdr, 24)), blockCount = CInt(BitConverter.ToUInt32(hdr, 28))
            hashTable = ReadTable(baseOff + hashPos, hashCount * 4, HashString("(hash table)", 3))
            blockTable = ReadTable(baseOff + blockPos, blockCount * 4, HashString("(block table)", 3))
        End Sub

        Private Function ReadTable(pos As Long, count As Integer, key As UInteger) As UInteger()
            Dim b(count * 4 - 1) As Byte
            fs.Position = pos
            fs.ReadExactly(b, 0, b.Length)
            Dim u(count - 1) As UInteger
            Buffer.BlockCopy(b, 0, u, 0, b.Length)
            Decrypt(u, key)
            Return u
        End Function

        Private Function FindBlock(name As String) As Integer
            Dim n = hashTable.Length \ 4
            If n = 0 Then Return -1
            Dim start = CInt(HashString(name, 0) Mod CUInt(n))
            Dim a = HashString(name, 1), b = HashString(name, 2)
            For i = 0 To n - 1
                Dim e = ((start + i) Mod n) * 4
                Dim blk = hashTable(e + 3)
                If blk = &HFFFFFFFFUI Then Return -1
                If hashTable(e) = a AndAlso hashTable(e + 1) = b AndAlso blk <> &HFFFFFFFEUI Then Return CInt(blk)
            Next
            Return -1
        End Function

        ''' <summary>The file's bytes, or Nothing if the archive doesn't have it.</summary>
        Public Function ReadFile(name As String) As Byte()
            Dim bi = FindBlock(name)
            If bi < 0 OrElse bi * 4 + 3 >= blockTable.Length Then Return Nothing
            Dim offset = blockTable(bi * 4), packed = CInt(blockTable(bi * 4 + 1)), size = CInt(blockTable(bi * 4 + 2)), flags = blockTable(bi * 4 + 3)
            If (flags And &H80000000UI) = 0 Then Return Nothing
            Dim raw(packed - 1) As Byte
            fs.Position = baseOff + offset
            fs.ReadExactly(raw, 0, packed)
            Dim encrypted = (flags And &H10000UI) <> 0
            Dim key As UInteger = 0
            If encrypted Then
                key = HashString(name.Substring(name.LastIndexOf("\"c) + 1), 3)
                If (flags And &H20000UI) <> 0 Then key = (key + offset) Xor CUInt(size)
            End If
            Dim compressed = (flags And &H200UI) <> 0, imploded = (flags And &H100UI) <> 0
            Dim oneUnit = (flags And &H1000000UI) <> 0
            If oneUnit Then
                If encrypted Then DecryptBytes(raw, key)
                Return If(compressed OrElse imploded, Unpack(raw, size, imploded), raw)
            End If
            If Not compressed AndAlso Not imploded Then
                If encrypted Then
                    Dim outb(size - 1) As Byte
                    For s = 0 To (size - 1) \ sectorSize
                        Dim len = Math.Min(sectorSize, size - s * sectorSize)
                        Dim chunk = raw.Skip(s * sectorSize).Take(len).ToArray()
                        DecryptBytes(chunk, key + CUInt(s))
                        Buffer.BlockCopy(chunk, 0, outb, s * sectorSize, len)
                    Next
                    Return outb
                End If
                Return raw.Take(size).ToArray()
            End If
            Dim sectors = (size + sectorSize - 1) \ sectorSize
            Dim offs(sectors) As UInteger
            Buffer.BlockCopy(raw, 0, offs, 0, (sectors + 1) * 4)
            If encrypted Then Decrypt(offs, key - 1UI)
            Dim result(size - 1) As Byte
            For s = 0 To sectors - 1
                Dim a = CInt(offs(s)), e = CInt(offs(s + 1))
                Dim chunk = raw.Skip(a).Take(e - a).ToArray()
                If encrypted Then DecryptBytes(chunk, key + CUInt(s))
                Dim want = Math.Min(sectorSize, size - s * sectorSize)
                Dim dec = If(chunk.Length < want, Unpack(chunk, want, imploded), chunk)
                Buffer.BlockCopy(dec, 0, result, s * sectorSize, Math.Min(want, dec.Length))
            Next
            Return result
        End Function

        Private Shared Function Unpack(chunk As Byte(), want As Integer, imploded As Boolean) As Byte()
            If imploded Then Return Explode.Run(chunk, 0, want)
            Dim mask = chunk(0)
            Dim data = chunk.Skip(1).ToArray()
            If (mask And &H8) <> 0 Then data = Explode.Run(data, 0, want)
            If (mask And &H2) <> 0 Then
                Using inp As New MemoryStream(data), z As New ZLibStream(inp, CompressionMode.Decompress), o As New MemoryStream()
                    z.CopyTo(o)
                    data = o.ToArray()
                End Using
            End If
            If (mask And Not CByte(&HA)) <> 0 Then Throw New NotSupportedException($"compression {mask:X2} is not supported")
            Return data
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            fs.Dispose()
        End Sub
    End Class

    ''' <summary>PKWARE Data Compression Library "explode" (port of zlib's contrib/blast).</summary>
    Public Module Explode
        Private Const MAXBITS = 13

        Private Class Huff
            Public Count(MAXBITS) As Short
            Public Symbol(255) As Short
        End Class

        Private ReadOnly LitLen As Byte() = {11, 124, 8, 7, 28, 7, 188, 13, 76, 4, 10, 8, 12, 10, 12, 10, 8, 23, 8,
            9, 7, 6, 7, 8, 7, 6, 55, 8, 23, 24, 12, 11, 7, 9, 11, 12, 6, 7, 22, 5,
            7, 24, 6, 11, 9, 6, 7, 22, 7, 11, 38, 7, 9, 8, 25, 11, 8, 11, 9, 12,
            8, 12, 5, 38, 5, 38, 5, 11, 7, 5, 6, 21, 6, 10, 53, 8, 7, 24, 10, 27,
            44, 253, 253, 253, 252, 252, 252, 13, 12, 45, 12, 45, 12, 61, 12, 45,
            44, 173}
        Private ReadOnly LenLen As Byte() = {2, 35, 36, 53, 38, 23}
        Private ReadOnly DistLen As Byte() = {2, 20, 53, 230, 247, 151, 248}
        Private ReadOnly BaseLen As Short() = {3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264}
        Private ReadOnly ExtraBits As Byte() = {0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8}

        Private ReadOnly LitCode As Huff = Construct(LitLen)
        Private ReadOnly LenCode As Huff = Construct(LenLen)
        Private ReadOnly DistCode As Huff = Construct(DistLen)

        Private Function Construct(rep As Byte()) As Huff
            Dim h As New Huff()
            Dim length(255) As Short
            Dim symbol = 0
            For Each r In rep
                Dim left = (r >> 4) + 1, len = r And 15
                For k = 1 To left : length(symbol) = CShort(len) : symbol += 1 : Next
            Next
            Dim n = symbol
            For s = 0 To n - 1 : h.Count(length(s)) += 1S : Next
            Dim offs(MAXBITS + 1) As Short
            For l = 1 To MAXBITS - 1 : offs(l + 1) = CShort(offs(l) + h.Count(l)) : Next
            For s = 0 To n - 1
                If length(s) <> 0 Then h.Symbol(offs(length(s))) = CShort(s) : offs(length(s)) += 1S
            Next
            Return h
        End Function

        Private Class Bits
            Public Src As Byte(), Pos As Integer, Buf As Integer, Cnt As Integer
            Public Function Take(need As Integer) As Integer
                While Cnt < need
                    If Pos >= Src.Length Then Throw New InvalidDataException("PKWARE data ended early")
                    Buf = Buf Or (CInt(Src(Pos)) << Cnt) : Pos += 1 : Cnt += 8
                End While
                Dim v = Buf And ((1 << need) - 1)
                Buf >>= need : Cnt -= need
                Return v
            End Function
        End Class

        Private Function Decode(s As Bits, h As Huff) As Integer
            Dim code = 0, first = 0, index = 0
            For bl = 1 To MAXBITS
                code = code Or (s.Take(1) Xor 1)
                Dim count = h.Count(bl)
                If code < first + count Then Return h.Symbol(index + (code - first))
                index += count : first += count
                first <<= 1 : code <<= 1
            Next
            Throw New InvalidDataException("bad PKWARE code")
        End Function

        Public Function Run(src As Byte(), start As Integer, want As Integer) As Byte()
            Dim s As New Bits With {.Src = src, .Pos = start}
            Dim lit = s.Take(8), dict = s.Take(8)
            If lit > 1 OrElse dict < 4 OrElse dict > 6 Then Throw New InvalidDataException("bad PKWARE header")
            Dim outp As New List(Of Byte)(want)
            While outp.Count < want
                If s.Take(1) = 1 Then
                    Dim sym = Decode(s, LenCode)
                    Dim len = BaseLen(sym) + s.Take(ExtraBits(sym))
                    If len = 519 Then Exit While
                    Dim sh = If(len = 2, 2, dict)
                    Dim dist = (Decode(s, DistCode) << sh) + s.Take(sh) + 1
                    If dist > outp.Count Then Throw New InvalidDataException("bad PKWARE distance")
                    For k = 1 To len : outp.Add(outp(outp.Count - dist)) : Next
                Else
                    outp.Add(CByte(If(lit = 1, Decode(s, LitCode), s.Take(8))))
                End If
            End While
            Return outp.ToArray()
        End Function
    End Module

End Namespace
