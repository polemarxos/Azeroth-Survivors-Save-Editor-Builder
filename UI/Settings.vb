Imports System.IO
Imports System.Text.Json

Namespace UI

    ''' <summary>Remembers the last files used, in %AppData%\AzsEditorBuilder\settings.json.</summary>
    Public Class Settings
        Public Property ExePath As String = ""
        Public Property SavePath As String = ""
        Public Property TemplatePath As String = ""
        Public Property OutDir As String = ""
        Public Property OpenAfterBuild As Boolean = True

        Private Shared ReadOnly Dir As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzsEditorBuilder")
        Private Shared ReadOnly FilePath As String = Path.Combine(Dir, "settings.json")

        Public Shared Function Load() As Settings
            Try
                If File.Exists(FilePath) Then Return JsonSerializer.Deserialize(Of Settings)(File.ReadAllText(FilePath))
            Catch
            End Try
            Return New Settings()
        End Function

        Public Sub Save()
            Try
                Directory.CreateDirectory(Dir)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Me, New JsonSerializerOptions With {.WriteIndented = True}))
            Catch
            End Try
        End Sub

        Public Shared ReadOnly Property DefaultOutDir As String
            Get
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Azeroth Survivors Save Editor")
            End Get
        End Property
    End Class

End Namespace
