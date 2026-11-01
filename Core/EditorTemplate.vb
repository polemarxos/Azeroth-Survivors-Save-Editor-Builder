Imports System.IO
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

Namespace Core

    ''' <summary>
    ''' Reads and rewrites the data tables inside the save editor's index.html
    ''' (lines such as <c>const PETS={...};</c>). Only the literal after "=" is replaced,
    ''' so the rest of the page (code, styles, comments) stays exactly as it was.
    ''' </summary>
    Public Class EditorTemplate

        Public Property Html As String

        Public Sub New(html As String)
            Me.Html = html.Replace(vbCrLf, vbLf)
        End Sub

        Public Shared Function LoadBuiltIn() As EditorTemplate
            Dim asm = Reflection.Assembly.GetExecutingAssembly()
            Dim name = asm.GetManifestResourceNames().FirstOrDefault(Function(n) n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
            If name Is Nothing Then Throw New InvalidOperationException("The built-in editor template is missing from this build.")
            Using s = asm.GetManifestResourceStream(name), r As New StreamReader(s, Encoding.UTF8)
                Return New EditorTemplate(r.ReadToEnd())
            End Using
        End Function

        Public Shared ReadOnly JsonOut As New JsonSerializerOptions With {
            .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            .WriteIndented = False
        }

        ' ---------- locating a literal ----------

        ''' <summary>Finds "const NAME=" (or "IC.ab=") at the start of a line; returns start/length of the literal.</summary>
        Private Function Locate(name As String, ByRef start As Integer, ByRef length As Integer) As Boolean
            Dim decl = If(name.Contains("."), Regex.Escape(name), "const\s+" & Regex.Escape(name)) & "\s*="
            Dim m = Regex.Match(Html, "(?m)^\s*" & decl)
            If Not m.Success Then Return False
            Dim i = m.Index + m.Length
            While i < Html.Length AndAlso Char.IsWhiteSpace(Html(i)) : i += 1 : End While
            Dim e = EndOfLiteral(Html, i)
            If e < 0 Then Return False
            start = i : length = e - i
            Return True
        End Function

        ''' <summary>Index just past a JS literal starting at i ({..}, [..], "..", number, etc.).</summary>
        Public Shared Function EndOfLiteral(s As String, i As Integer) As Integer
            Dim c = s(i)
            If c = """"c OrElse c = "'"c OrElse c = "`"c Then Return SkipString(s, i)
            If c = "{"c OrElse c = "["c Then
                Dim depth = 0, j = i
                While j < s.Length
                    Dim ch = s(j)
                    If ch = """"c OrElse ch = "'"c OrElse ch = "`"c Then
                        j = SkipString(s, j) : Continue While
                    End If
                    If ch = "{"c OrElse ch = "["c OrElse ch = "("c Then depth += 1
                    If ch = "}"c OrElse ch = "]"c OrElse ch = ")"c Then
                        depth -= 1
                        If depth = 0 Then Return j + 1
                    End If
                    j += 1
                End While
                Return -1
            End If
            ' plain token up to ; or end of line
            Dim k = i
            While k < s.Length AndAlso s(k) <> ";"c AndAlso s(k) <> vbLf(0) : k += 1 : End While
            Return k
        End Function

        Private Shared Function SkipString(s As String, i As Integer) As Integer
            Dim q = s(i), j = i + 1
            While j < s.Length
                If s(j) = "\"c Then j += 2 : Continue While
                If s(j) = q Then Return j + 1
                j += 1
            End While
            Return s.Length
        End Function

        ' ---------- JS literal <-> JSON ----------

        ''' <summary>Turns a simple JS object/array literal into JSON (quotes bare keys, converts single-quoted strings).</summary>
        Public Shared Function JsToJson(js As String) As String
            Dim sb As New StringBuilder(js.Length + 64)
            Dim stack As New Stack(Of Char)
            Dim i = 0
            While i < js.Length
                Dim c = js(i)
                If c = """"c Then
                    Dim e = SkipString(js, i)
                    sb.Append(js, i, e - i) : i = e : Continue While
                End If
                If c = "'"c Then
                    Dim e = SkipString(js, i)
                    Dim inner = js.Substring(i + 1, Math.Max(0, e - i - 2)).Replace("\'", "'").Replace("""", "\""")
                    sb.Append(""""c).Append(inner).Append(""""c) : i = e : Continue While
                End If
                If c = "["c Then stack.Push(c)
                If (c = "}"c OrElse c = "]"c) AndAlso stack.Count > 0 Then stack.Pop()
                If c = "{"c OrElse c = ","c Then
                    If c = "{"c Then stack.Push(c)
                    sb.Append(c) : i += 1
                    ' look for a bare key
                    Dim j = i
                    While j < js.Length AndAlso Char.IsWhiteSpace(js(j)) : j += 1 : End While
                    Dim k = j
                    While k < js.Length AndAlso (Char.IsLetterOrDigit(js(k)) OrElse js(k) = "_"c OrElse js(k) = "$"c) : k += 1 : End While
                    If k > j Then
                        Dim m = k
                        While m < js.Length AndAlso Char.IsWhiteSpace(js(m)) : m += 1 : End While
                        If m < js.Length AndAlso js(m) = ":"c AndAlso stack.Count > 0 AndAlso stack.Peek() = "{"c Then
                            sb.Append(js, i, j - i).Append(""""c).Append(js, j, k - j).Append(""""c)
                            i = k
                        End If
                    End If
                    Continue While
                End If
                sb.Append(c) : i += 1
            End While
            Return sb.ToString()
        End Function

        ' ---------- public API ----------

        Public Function HasConst(name As String) As Boolean
            Dim s = 0, l = 0
            Return Locate(name, s, l)
        End Function

        Public Function GetRaw(name As String) As String
            Dim s = 0, l = 0
            If Not Locate(name, s, l) Then Return Nothing
            Return Html.Substring(s, l)
        End Function

        Public Function GetJson(name As String) As JsonNode
            Dim raw = GetRaw(name)
            If raw Is Nothing Then Return Nothing
            Return JsonNode.Parse(JsToJson(raw))
        End Function

        Public Sub SetRaw(name As String, literal As String)
            Dim s = 0, l = 0
            If Not Locate(name, s, l) Then Throw New InvalidOperationException("The editor has no '" & name & "' table.")
            Html = Html.Substring(0, s) & literal & Html.Substring(s + l)
        End Sub

        Public Sub SetJson(name As String, node As JsonNode)
            SetRaw(name, node.ToJsonString(JsonOut))
        End Sub

        ' ---------- seal key ----------

        Private Shared ReadOnly KeyRx As New Regex("encode\(""(?<p>[^""]*)""\)\s*,\s*Uint8Array\.from\(""(?<h>[0-9a-fA-F]{32})""", RegexOptions.Singleline)

        Public Function GetSeal() As SaveSeal
            Dim m = KeyRx.Match(Html)
            If Not m.Success Then Return Nothing
            Return SaveSeal.FromHex(m.Groups("p").Value, m.Groups("h").Value)
        End Function

        Public Sub SetSeal(seal As SaveSeal)
            Dim m = KeyRx.Match(Html)
            If Not m.Success Then Throw New InvalidOperationException("Could not find the seal key in the editor.")
            Dim p = m.Groups("p"), h = m.Groups("h")
            Html = Html.Substring(0, p.Index) & seal.Prefix & Html.Substring(p.Index + p.Length, h.Index - (p.Index + p.Length)) &
                   seal.SecretHex & Html.Substring(h.Index + h.Length)
        End Sub

        ' ---------- version label ----------

        Public Sub StampVersion(Optional stamp As DateTime? = Nothing)
            Dim t = If(stamp, DateTime.Now).ToString("yyyy-MM-dd HH:mm")
            Html = Regex.Replace(Html, "(<span class=""ver"">)[^<]*(</span>)", "${1}Version " & t & "${2}")
        End Sub

        Public Function VersionLabel() As String
            Dim m = Regex.Match(Html, "<span class=""ver"">([^<]*)</span>")
            Return If(m.Success, m.Groups(1).Value, "")
        End Function

        Public Sub Save(path As String)
            File.WriteAllText(path, Html, New UTF8Encoding(False))
        End Sub
    End Class

End Namespace
