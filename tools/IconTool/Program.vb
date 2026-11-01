Module Program
    Function Main(args As String()) As Integer
        If args.Length < 2 OrElse Not IO.File.Exists(args(0)) Then
            Console.WriteLine("Usage: IconTool <game.exe> <out.ico>")
            Return 1
        End If
        If AzsEditorBuilder.Core.IconExtractor.ExtractToFile(args(0), args(1)) Then
            Console.WriteLine("Icon copied from " & IO.Path.GetFileName(args(0)) & " to " & args(1))
            Return 0
        End If
        Console.WriteLine("No icon found in " & args(0))
        Return 2
    End Function
End Module
