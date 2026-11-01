Imports System.IO
Imports System.Security.Cryptography
Imports System.Text

Namespace Core

    ''' <summary>
    ''' The "RSE1:" seal used by Azeroth Survivors saves (same algorithm as the web editor):
    ''' base64( header[16] | ciphertext | tag[20] ), keystream = HMAC-SHA1(key, "ks" | header | le64(i)),
    ''' tag = HMAC-SHA1(key, "tag" | header | ciphertext). Key = prefix text + 16 secret bytes.
    ''' </summary>
    Public Class SaveSeal

        Public Property Prefix As String
        Public Property Secret As Byte()

        Public Sub New(prefix As String, secret As Byte())
            Me.Prefix = prefix
            Me.Secret = secret
        End Sub

        Public Shared Function FromHex(prefix As String, hex As String) As SaveSeal
            Return New SaveSeal(prefix, Convert.FromHexString(hex))
        End Function

        Public ReadOnly Property SecretHex As String
            Get
                Return Convert.ToHexString(Secret).ToLowerInvariant()
            End Get
        End Property

        Public Function KeyBytes() As Byte()
            Return Concat(Encoding.UTF8.GetBytes(Prefix), Secret)
        End Function

        Public Shared Function Concat(ParamArray parts As Byte()()) As Byte()
            Dim n = 0
            For Each p In parts : n += p.Length : Next
            Dim o(n - 1) As Byte, i = 0
            For Each p In parts
                Buffer.BlockCopy(p, 0, o, i, p.Length) : i += p.Length
            Next
            Return o
        End Function

        Private Function Keystream(key As Byte(), header As Byte(), n As Integer) As Byte()
            Dim o(n - 1) As Byte
            Using h As New HMACSHA1(key)
                Dim i As Long = 0, p = 0
                Dim ks = Encoding.ASCII.GetBytes("ks")
                While p < n
                    Dim blk = h.ComputeHash(Concat(ks, header, BitConverter.GetBytes(i)))
                    Dim c = Math.Min(20, n - p)
                    Buffer.BlockCopy(blk, 0, o, p, c)
                    p += 20 : i += 1
                End While
            End Using
            Return o
        End Function

        Private Shared Function Tag(key As Byte(), header As Byte(), ct As Byte()) As Byte()
            Using h As New HMACSHA1(key)
                Return h.ComputeHash(Concat(Encoding.ASCII.GetBytes("tag"), header, ct))
            End Using
        End Function

        ''' <summary>Splits a sealed save. Returns Nothing if it is not a RSE1 save.</summary>
        Public Shared Function Split(text As String, ByRef header As Byte(), ByRef ct As Byte(), ByRef tg As Byte()) As Boolean
            text = If(text, "").Trim()
            If Not text.StartsWith("RSE1:") Then Return False
            Dim raw As Byte()
            Try
                raw = Convert.FromBase64String(text.Substring(5).Replace(vbCr, "").Replace(vbLf, "").Replace(" ", ""))
            Catch
                Return False
            End Try
            If raw.Length < 36 Then Return False
            header = raw.Take(16).ToArray()
            ct = raw.Skip(16).Take(raw.Length - 36).ToArray()
            tg = raw.Skip(raw.Length - 20).ToArray()
            Return True
        End Function

        ''' <summary>True when this key produces the save's tag (i.e. the key is right).</summary>
        Public Function Verifies(saveText As String) As Boolean
            Dim h As Byte() = Nothing, c As Byte() = Nothing, t As Byte() = Nothing
            If Not Split(saveText, h, c, t) Then Return False
            Return Tag(KeyBytes(), h, c).SequenceEqual(t)
        End Function

        Public Function Unseal(saveText As String, ByRef tagOk As Boolean) As String
            Dim h As Byte() = Nothing, c As Byte() = Nothing, t As Byte() = Nothing
            If Not Split(saveText, h, c, t) Then Throw New InvalidDataException("This is not a sealed Azeroth Survivors save (it should start with RSE1:).")
            Dim key = KeyBytes()
            tagOk = Tag(key, h, c).SequenceEqual(t)
            Dim ks = Keystream(key, h, c.Length)
            Dim pt(c.Length - 1) As Byte
            For i = 0 To c.Length - 1 : pt(i) = c(i) Xor ks(i) : Next
            Return New UTF8Encoding(False, True).GetString(pt)
        End Function

        Public Function Seal(plain As String, Optional header As Byte() = Nothing) As String
            If header Is Nothing Then header = RandomNumberGenerator.GetBytes(16)
            Dim key = KeyBytes()
            Dim pt = New UTF8Encoding(False).GetBytes(plain)
            Dim ks = Keystream(key, header, pt.Length)
            Dim ct(pt.Length - 1) As Byte
            For i = 0 To pt.Length - 1 : ct(i) = pt(i) Xor ks(i) : Next
            Return "RSE1:" & Convert.ToBase64String(Concat(header, ct, Tag(key, header, ct)))
        End Function
    End Class

End Namespace
