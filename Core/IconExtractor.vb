Imports System.IO

Namespace Core

    ''' <summary>
    ''' Copies the main icon (all sizes) out of a Windows .exe into a .ico file.
    ''' Reads the PE resource section directly, so it needs no Windows API and works on any file.
    ''' </summary>
    Public Module IconExtractor

        Private Const RT_ICON As Integer = 3
        Private Const RT_GROUP_ICON As Integer = 14

        ''' <summary>Returns the .ico bytes of the exe's first (main) icon, or Nothing if it has none.</summary>
        Public Function ExtractMainIcon(exePath As String) As Byte()
            Return ExtractMainIcon(File.ReadAllBytes(exePath))
        End Function

        Public Function ExtractMainIcon(pe As Byte()) As Byte()
            Try
                Dim r As New PeResources(pe)
                Dim groups = r.Entries(RT_GROUP_ICON)
                If groups.Count = 0 Then Return Nothing
                Dim grp = groups(0).Data          ' Explorer shows the first icon group
                Dim count = BitConverter.ToUInt16(grp, 4)
                Dim icons = r.Entries(RT_ICON).ToDictionary(Function(e) e.Id, Function(e) e.Data)

                Dim images As New List(Of (Entry As Byte(), Img As Byte()))
                For i = 0 To count - 1
                    Dim p = 6 + i * 14
                    Dim id = BitConverter.ToUInt16(grp, p + 12)
                    Dim img As Byte() = Nothing
                    If Not icons.TryGetValue(id, img) Then Continue For
                    Dim entry(15) As Byte
                    Array.Copy(grp, p, entry, 0, 8)                                   ' width, height, colours, reserved, planes, bitcount
                    Array.Copy(BitConverter.GetBytes(CUInt(img.Length)), 0, entry, 8, 4)
                    images.Add((entry, img))
                Next
                If images.Count = 0 Then Return Nothing

                Using ms As New MemoryStream()
                    Using w As New BinaryWriter(ms)
                        w.Write(CUShort(0)) : w.Write(CUShort(1)) : w.Write(CUShort(images.Count))
                        Dim offset = 6 + 16 * images.Count
                        For Each im In images
                            w.Write(im.Entry, 0, 12)
                            w.Write(CUInt(offset))
                            offset += im.Img.Length
                        Next
                        For Each im In images : w.Write(im.Img) : Next
                    End Using
                    Return ms.ToArray()
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Function ExtractToFile(exePath As String, icoPath As String) As Boolean
            Dim ico = ExtractMainIcon(exePath)
            If ico Is Nothing Then Return False
            File.WriteAllBytes(icoPath, ico)
            Return True
        End Function

        ''' <summary>Minimal reader for the resource tree of a PE file (type / name / language).</summary>
        Private Class PeResources
            Private ReadOnly B As Byte()
            Private ReadOnly RsrcOff As Integer
            Private ReadOnly Sections As New List(Of (Va As Integer, Size As Integer, Raw As Integer))

            Public Sub New(pe As Byte())
                B = pe
                If U16(0) <> &H5A4D Then Throw New InvalidDataException("Not an exe file.")
                Dim peOff = I32(&H3C)
                If I32(peOff) <> &H4550 Then Throw New InvalidDataException("Not a PE file.")
                Dim nSec = U16(peOff + 6)
                Dim optSize = U16(peOff + 20)
                Dim opt = peOff + 24
                Dim magic = U16(opt)
                Dim dirs = opt + If(magic = &H20B, 112, 96)
                Dim rsrcRva = I32(dirs + 2 * 8)
                Dim sec = opt + optSize
                For i = 0 To nSec - 1
                    Dim s = sec + i * 40
                    Sections.Add((I32(s + 12), Math.Max(I32(s + 8), I32(s + 16)), I32(s + 20)))
                Next
                RsrcOff = If(rsrcRva = 0, -1, RvaToOff(rsrcRva))
            End Sub

            Private Function U16(o As Integer) As Integer
                Return BitConverter.ToUInt16(B, o)
            End Function
            Private Function I32(o As Integer) As Integer
                Return BitConverter.ToInt32(B, o)
            End Function

            Private Function RvaToOff(rva As Integer) As Integer
                For Each s In Sections
                    If rva >= s.Va AndAlso rva < s.Va + s.Size Then Return rva - s.Va + s.Raw
                Next
                Return -1
            End Function

            ''' <summary>All resources of one type: (id, data). Named entries get id -1. Uses the first language.</summary>
            Public Function Entries(type As Integer) As List(Of (Id As Integer, Data As Byte()))
                Dim res As New List(Of (Id As Integer, Data As Byte()))
                If RsrcOff < 0 Then Return res
                For Each t In Dir(RsrcOff)
                    If t.Id <> type OrElse Not t.IsDir Then Continue For
                    For Each n In Dir(RsrcOff + t.Target)
                        Dim leaf = n
                        If n.IsDir Then
                            Dim langs = Dir(RsrcOff + n.Target)
                            If langs.Count = 0 Then Continue For
                            leaf = langs(0)
                        End If
                        Dim de = RsrcOff + leaf.Target
                        Dim off = RvaToOff(I32(de))
                        Dim size = I32(de + 4)
                        If off < 0 OrElse size <= 0 OrElse off + size > B.Length Then Continue For
                        Dim d(size - 1) As Byte
                        Array.Copy(B, off, d, 0, size)
                        res.Add((n.Id, d))
                    Next
                Next
                Return res
            End Function

            Private Function Dir(o As Integer) As List(Of (Id As Integer, IsDir As Boolean, Target As Integer))
                Dim l As New List(Of (Id As Integer, IsDir As Boolean, Target As Integer))
                Dim n = U16(o + 12) + U16(o + 14)
                For i = 0 To n - 1
                    Dim e = o + 16 + i * 8
                    Dim name = I32(e), off = I32(e + 4)
                    Dim id = If((name And &H80000000) <> 0, -1, name And &HFFFF)
                    l.Add((id, (off And &H80000000) <> 0, off And &H7FFFFFFF))
                Next
                Return l
            End Function
        End Class
    End Module

End Namespace
