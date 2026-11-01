Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports AzsEditorBuilder.Core

Namespace UI

    Public Class MainForm
        Inherits Form

        Private ReadOnly cfg As Settings = Settings.Load()

        ' scan tab
        Private ReadOnly txtExe As New TextBox()
        Private ReadOnly txtSave As New TextBox()
        Private ReadOnly txtTemplate As New TextBox()
        Private ReadOnly lblTemplate As New Label()
        Private ReadOnly btnScan As New Button()
        Private ReadOnly prog As New ProgressBar()
        Private ReadOnly txtLog As New TextBox()

        ' review tab
        Private ReadOnly grid As New DataGridView()
        Private ReadOnly chkNewOnly As New CheckBox()
        Private ReadOnly lblSummary As New Label()
        Private ReadOnly rows As New BindingList(Of Finding)()
        Private allFindings As New List(Of Finding)

        ' build tab
        Private ReadOnly txtOut As New TextBox()
        Private ReadOnly chkOpen As New CheckBox()
        Private ReadOnly txtBuildLog As New TextBox()
        Private ReadOnly btnBuild As New Button()

        ' save tab
        Private ReadOnly txtSaveText As New TextBox()
        Private ReadOnly lblSaveState As New Label()
        Private saveHeader As String = ""

        Private ReadOnly tabs As New TabControl()
        Private scanner As ExeScanner
        Private keyResult As KeyResult
        Private template As EditorTemplate

        Public Sub New()
            Text = "Azeroth Survivors - Save Editor Builder"
            Font = New Font("Segoe UI", 9.5F)
            MinimumSize = New Size(900, 600)
            Size = New Size(1150, 760)
            StartPosition = FormStartPosition.CenterScreen

            tabs.Dock = DockStyle.Fill
            tabs.TabPages.Add(BuildScanTab())
            tabs.TabPages.Add(BuildReviewTab())
            tabs.TabPages.Add(BuildBuildTab())
            tabs.TabPages.Add(BuildSaveTab())
            Controls.Add(tabs)

            txtExe.Text = cfg.ExePath
            txtSave.Text = cfg.SavePath
            txtTemplate.Text = cfg.TemplatePath
            txtOut.Text = If(String.IsNullOrWhiteSpace(cfg.OutDir), Settings.DefaultOutDir, cfg.OutDir)
            chkOpen.Checked = cfg.OpenAfterBuild
            UpdateTemplateLabel()
            ApplyIcon()
        End Sub

        Private hasOwnIcon As Boolean

        ''' <summary>
        ''' Window icon: the Azeroth Survivors icon built into this program (app.ico), or, if the program
        ''' was built without one, the icon read straight from the game exe the user picked.
        ''' </summary>
        Private Sub ApplyIcon()
            Try
                If Not hasOwnIcon Then
                    Dim st = Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("AzsEditorBuilder.app.ico")
                    If st IsNot Nothing Then
                        Using st
                            Icon = New Icon(st)
                        End Using
                        hasOwnIcon = True
                        Return
                    End If
                Else
                    Return
                End If
                Dim exe = txtExe.Text.Trim()
                If exe <> "" AndAlso File.Exists(exe) Then
                    Dim ico = IconExtractor.ExtractMainIcon(exe)
                    If ico IsNot Nothing Then Icon = New Icon(New MemoryStream(ico))
                End If
            Catch
                ' keep the default icon
            End Try
        End Sub

        Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
            cfg.ExePath = txtExe.Text.Trim()
            cfg.SavePath = txtSave.Text.Trim()
            cfg.TemplatePath = txtTemplate.Text.Trim()
            cfg.OutDir = txtOut.Text.Trim()
            cfg.OpenAfterBuild = chkOpen.Checked
            cfg.Save()
            MyBase.OnFormClosing(e)
        End Sub

        ' ============================== layout helpers ==============================

        Private Shared Function Btn(caption As String, handler As EventHandler) As Button
            Dim b As New Button With {.Text = caption, .AutoSize = True, .Padding = New Padding(8, 3, 8, 3), .Margin = New Padding(4)}
            AddHandler b.Click, handler
            Return b
        End Function

        Private Shared Function Lbl(caption As String) As Label
            Return New Label With {.Text = caption, .AutoSize = True, .Anchor = AnchorStyles.Left, .Margin = New Padding(4, 8, 4, 4)}
        End Function

        Private Shared Function Hint(caption As String) As Label
            Return New Label With {.Text = caption, .AutoSize = True, .ForeColor = SystemColors.GrayText, .Margin = New Padding(4, 0, 4, 8), .MaximumSize = New Size(1000, 0)}
        End Function

        Private Shared Sub SetupLog(t As TextBox)
            t.Multiline = True
            t.ReadOnly = True
            t.ScrollBars = ScrollBars.Both
            t.WordWrap = False
            t.Dock = DockStyle.Fill
            t.Font = New Font("Consolas", 9.5F)
            t.BackColor = SystemColors.Window
        End Sub

        ''' <summary>A row "label | textbox | buttons" inside a 3-column table.</summary>
        Private Shared Sub FileRow(t As TableLayoutPanel, caption As String, box As TextBox, ParamArray buttons As Button())
            Dim r = t.RowCount
            t.RowCount += 1
            t.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            t.Controls.Add(Lbl(caption), 0, r)
            box.Dock = DockStyle.Fill
            box.Margin = New Padding(4, 6, 4, 4)
            t.Controls.Add(box, 1, r)
            Dim f As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = New Padding(0)}
            f.Controls.AddRange(buttons)
            t.Controls.Add(f, 2, r)
        End Sub

        Private Shared Sub SpanRow(t As TableLayoutPanel, c As Control)
            Dim r = t.RowCount
            t.RowCount += 1
            t.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            t.Controls.Add(c, 0, r)
            t.SetColumnSpan(c, 3)
        End Sub

        Private Shared Function NewTable() As TableLayoutPanel
            Dim t As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 3, .Padding = New Padding(8)}
            t.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            t.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
            t.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
            Return t
        End Function

        ' ============================== 1. scan ==============================

        Private Function BuildScanTab() As TabPage
            Dim page As New TabPage("1. Scan the game")
            Dim t = NewTable()
            SpanRow(t, Hint("Pick the game's AzerothSurvivors.exe. The builder reads the data tables inside it (achievements, zones, pets, tabards, perks, codex) " &
                            "and compares them with the save editor. Everything happens on this PC; nothing is sent anywhere."))
            FileRow(t, "Game exe:", txtExe, Btn("Browse...", Sub()
                                                                 PickFile(txtExe, "Game executable|*.exe|All files|*.*")
                                                                 ApplyIcon()
                                                             End Sub))
            FileRow(t, "Save (optional):", txtSave, Btn("Browse...", Sub() PickFile(txtSave, "Azeroth Survivors save|*.sav;*.txt|All files|*.*")),
                    Btn("Clear", Sub() txtSave.Text = ""))
            SpanRow(t, Hint("A save made by this version of the game lets the builder confirm the save seal key. Without one, a changed key can only be guessed."))
            FileRow(t, "Editor to update:", txtTemplate, Btn("Browse...", Sub()
                                                                               PickFile(txtTemplate, "Save editor page|index.html;*.html;*.htm|All files|*.*")
                                                                               UpdateTemplateLabel()
                                                                           End Sub),
                    Btn("Automatic", Sub()
                                         txtTemplate.Text = ""
                                         UpdateTemplateLabel()
                                     End Sub))
            lblTemplate.AutoSize = True
            lblTemplate.ForeColor = SystemColors.GrayText
            lblTemplate.Margin = New Padding(4, 0, 4, 8)
            SpanRow(t, lblTemplate)

            btnScan.Text = "Scan game"
            btnScan.AutoSize = True
            btnScan.Padding = New Padding(14, 5, 14, 5)
            btnScan.Font = New Font(Font, FontStyle.Bold)
            AddHandler btnScan.Click, AddressOf DoScan
            prog.Style = ProgressBarStyle.Marquee
            prog.MarqueeAnimationSpeed = 0
            prog.Width = 260
            prog.Margin = New Padding(12, 10, 4, 4)
            Dim f As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill}
            f.Controls.Add(btnScan)
            f.Controls.Add(prog)
            f.Controls.Add(Btn("Export strings dump...", AddressOf DoDump))
            SpanRow(t, f)

            SetupLog(txtLog)
            page.Controls.Add(txtLog)
            page.Controls.Add(t)
            Return page
        End Function

        Private Sub PickFile(box As TextBox, filter As String)
            Using d As New OpenFileDialog With {.Filter = filter}
                Try
                    If box.Text <> "" AndAlso File.Exists(box.Text) Then d.InitialDirectory = Path.GetDirectoryName(box.Text)
                Catch
                End Try
                If d.ShowDialog(Me) = DialogResult.OK Then box.Text = d.FileName
            End Using
        End Sub

        ''' <summary>The editor page that will be updated: chosen file, else the last one built, else the copy inside this program.</summary>
        Private Function TemplateSource() As String
            Dim p = txtTemplate.Text.Trim()
            If p <> "" Then Return p
            Dim last = Path.Combine(txtOut.Text.Trim(), "index.html")
            If txtOut.Text.Trim() <> "" AndAlso File.Exists(last) Then Return last
            Return ""
        End Function

        Private Function LoadTemplate() As EditorTemplate
            Dim src = TemplateSource()
            If src = "" Then Return EditorTemplate.LoadBuiltIn()
            Return New EditorTemplate(File.ReadAllText(src, Encoding.UTF8))
        End Function

        Private Sub UpdateTemplateLabel()
            Try
                Dim src = TemplateSource()
                Dim t = LoadTemplate()
                lblTemplate.Text = If(src = "", "Using the editor built into this program", "Using " & src) & " (" & t.VersionLabel() & ")"
            Catch ex As Exception
                lblTemplate.Text = "Cannot read the editor page: " & ex.Message
            End Try
        End Sub

        Private Sub Log(s As String)
            If txtLog.InvokeRequired Then
                txtLog.BeginInvoke(Sub() Log(s))
                Return
            End If
            txtLog.AppendText(s & Environment.NewLine)
        End Sub

        Private Async Sub DoScan(sender As Object, e As EventArgs)
            Dim exe = txtExe.Text.Trim()
            If Not File.Exists(exe) Then
                MessageBox.Show(Me, "Pick the game's .exe first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Dim savePath = txtSave.Text.Trim()
            If savePath <> "" AndAlso Not File.Exists(savePath) Then
                MessageBox.Show(Me, "The save file was not found.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            Try
                template = LoadTemplate()
            Catch ex As Exception
                MessageBox.Show(Me, "Cannot read the editor page:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
                Return
            End Try

            btnScan.Enabled = False
            prog.MarqueeAnimationSpeed = 30
            txtLog.Clear()
            Log("Editor: " & template.VersionLabel())
            Dim tpl = template
            Try
                Dim result = Await Task.Run(Function()
                                                Dim known = KnownData.FromTemplate(tpl)
                                                Dim sc As New ExeScanner(AddressOf Log)
                                                sc.Load(exe)
                                                Dim saveText = If(savePath <> "", File.ReadAllText(savePath), Nothing)
                                                Dim key = sc.FindKey(known.Seal, saveText)
                                                Dim found = sc.Analyse(known)
                                                Return (sc, key, found)
                                            End Function)
                scanner = result.sc
                keyResult = result.key
                allFindings = result.found
                If keyResult.Changed AndAlso keyResult.Seal IsNot Nothing Then
                    allFindings.Insert(0, New Finding With {
                        .Category = Cat.Key, .Id = keyResult.Seal.Prefix & keyResult.Seal.SecretHex, .Name = "New save seal key",
                        .Include = keyResult.Verified, .Notes = keyResult.Note})
                End If
                Dim nNew = allFindings.Where(Function(f) f.Status = FindStatus.New).Count()
                Dim nMiss = allFindings.Where(Function(f) f.Status = FindStatus.Missing).Count()
                Log("")
                Log($"Done: {nNew} new, {nMiss} missing. Check them on the Review tab, then build on the Build tab.")
                FillGrid()
                If nNew + nMiss > 0 Then tabs.SelectedIndex = 1
            Catch ex As Exception
                Log("Scan failed: " & ex.Message)
                MessageBox.Show(Me, "Scan failed:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
            Finally
                btnScan.Enabled = True
                prog.MarqueeAnimationSpeed = 0
            End Try
        End Sub

        Private Sub DoDump(sender As Object, e As EventArgs)
            If scanner Is Nothing Then
                MessageBox.Show(Me, "Scan the game first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            Using d As New SaveFileDialog With {.Filter = "Text file|*.txt", .FileName = "AzerothSurvivors_strings.txt"}
                If d.ShowDialog(Me) <> DialogResult.OK Then Return
                scanner.DumpStrings(d.FileName)
                Log("Strings written to " & d.FileName)
            End Using
        End Sub

        ' ============================== 2. review ==============================

        Private Function BuildReviewTab() As TabPage
            Dim page As New TabPage("2. Review changes")
            Dim top As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(6)}
            chkNewOnly.Text = "Show only new"
            chkNewOnly.AutoSize = True
            chkNewOnly.Checked = False
            chkNewOnly.Margin = New Padding(4, 9, 12, 4)
            AddHandler chkNewOnly.CheckedChanged, Sub() FillGrid()
            top.Controls.Add(chkNewOnly)
            top.Controls.Add(Btn("Tick all new", Sub() SetAll(True)))
            top.Controls.Add(Btn("Untick all", Sub() SetAll(False)))
            top.Controls.Add(Btn("Move up", Sub() MoveRow(-1)))
            top.Controls.Add(Btn("Move down", Sub() MoveRow(1)))
            top.Controls.Add(Btn("Save report...", AddressOf DoReport))
            lblSummary.AutoSize = True
            lblSummary.Margin = New Padding(12, 9, 4, 4)
            top.Controls.Add(lblSummary)

            Dim hintLbl = Hint("Ticked 'New' rows are written into the editor. You can fix names, descriptions, icons (WoW icon names) and the Extra column " &
                            "(achievement tab, perk max rank or codex rarity) before building. 'Missing' rows are only information. " &
                            "New zones are added after the last zone in the order shown; use Move up/down to change it.")
            hintLbl.Dock = DockStyle.Top
            hintLbl.Padding = New Padding(8, 0, 8, 4)

            grid.Dock = DockStyle.Fill
            grid.AutoGenerateColumns = False
            grid.AllowUserToAddRows = False
            grid.AllowUserToDeleteRows = False
            grid.RowHeadersVisible = False
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
            grid.MultiSelect = False
            grid.BackgroundColor = SystemColors.Window
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
            grid.Columns.Add(New DataGridViewCheckBoxColumn With {.DataPropertyName = "Include", .HeaderText = "Add", .Width = 44})
            AddCol("Status", "Status", 70, True)
            AddCol("Category", "Category", 150, True)
            AddCol("Id", "Id", 160, True)
            AddCol("Name", "Name", 180, False)
            AddCol("Description", "Description", 260, False)
            AddCol("Icon", "Icon", 170, False)
            AddCol("Extra", "Extra", 80, False)
            AddCol("Notes", "Notes", 280, True)
            grid.DataSource = rows
            AddHandler grid.CurrentCellDirtyStateChanged, Sub()
                                                              If grid.IsCurrentCellDirty Then grid.CommitEdit(DataGridViewDataErrorContexts.Commit)
                                                          End Sub
            AddHandler grid.CellFormatting, AddressOf GridFormat
            AddHandler grid.CellValueChanged, Sub() UpdateSummary()
            AddHandler grid.DataError, Sub(s, ev) ev.ThrowException = False

            page.Controls.Add(grid)
            page.Controls.Add(hintLbl)
            page.Controls.Add(top)
            Return page
        End Function

        Private Sub AddCol(prop As String, header As String, width As Integer, ro As Boolean)
            grid.Columns.Add(New DataGridViewTextBoxColumn With {.DataPropertyName = prop, .HeaderText = header, .Width = width, .ReadOnly = ro,
                             .SortMode = DataGridViewColumnSortMode.NotSortable})
        End Sub

        Private Sub GridFormat(sender As Object, e As DataGridViewCellFormattingEventArgs)
            If e.RowIndex < 0 OrElse e.RowIndex >= rows.Count Then Return
            Dim f = rows(e.RowIndex)
            Dim c = If(f.Status = FindStatus.New, If(f.Category = Cat.Key, Color.FromArgb(255, 243, 214), Color.FromArgb(226, 245, 232)),
                       If(f.Status = FindStatus.Missing, Color.FromArgb(253, 232, 232), SystemColors.Window))
            If Not Cat.Writable.Contains(f.Category) Then c = Color.FromArgb(240, 240, 240)
            e.CellStyle.BackColor = c
            e.CellStyle.ForeColor = Color.Black
        End Sub

        Private Sub FillGrid()
            rows.RaiseListChangedEvents = False
            rows.Clear()
            For Each f In allFindings
                If chkNewOnly.Checked AndAlso f.Status <> FindStatus.New Then Continue For
                rows.Add(f)
            Next
            rows.RaiseListChangedEvents = True
            rows.ResetBindings()
            UpdateSummary()
        End Sub

        Private Sub UpdateSummary()
            Dim ticked = allFindings.Where(Function(f) f.Include AndAlso f.Status = FindStatus.New AndAlso Cat.Writable.Contains(f.Category)).Count()
            lblSummary.Text = $"{allFindings.Count} rows, {ticked} ticked to add"
        End Sub

        Private Sub SetAll(onOff As Boolean)
            For Each f In allFindings
                f.Include = onOff AndAlso f.Status = FindStatus.New AndAlso Cat.Writable.Contains(f.Category)
            Next
            grid.Refresh()
            UpdateSummary()
        End Sub

        Private Sub MoveRow(d As Integer)
            If grid.CurrentRow Is Nothing Then Return
            Dim i = grid.CurrentRow.Index
            Dim j = i + d
            If j < 0 OrElse j >= rows.Count Then Return
            Dim a = rows(i), b = rows(j)
            Dim ia = allFindings.IndexOf(a), ib = allFindings.IndexOf(b)
            allFindings(ia) = b : allFindings(ib) = a
            FillGrid()
            grid.CurrentCell = grid.Rows(j).Cells(0)
        End Sub

        Private Sub DoReport(sender As Object, e As EventArgs)
            If allFindings.Count = 0 Then Return
            Using d As New SaveFileDialog With {.Filter = "Text file|*.txt", .FileName = "AzerothSurvivors_scan_report.txt"}
                If d.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim sb As New StringBuilder()
                sb.AppendLine(txtLog.Text)
                sb.AppendLine()
                For Each f In allFindings
                    sb.AppendLine(String.Join(vbTab, f.Status.ToString(), f.Category, f.Id, f.Name, f.Description, f.Icon, f.Extra, f.Notes))
                Next
                File.WriteAllText(d.FileName, sb.ToString(), New UTF8Encoding(False))
            End Using
        End Sub

        ' ============================== 3. build ==============================

        Private Function BuildBuildTab() As TabPage
            Dim page As New TabPage("3. Build editor")
            Dim t = NewTable()
            SpanRow(t, Hint("Writes the updated save editor as index.html (one file that works offline, on PC or phone). " &
                            "The version label is set to today's date and time. Upload this file to your GitHub page to update the online editor."))
            FileRow(t, "Output folder:", txtOut, Btn("Browse...", AddressOf PickOut), Btn("Open folder", AddressOf OpenOut))
            chkOpen.Text = "Open the editor in the browser after building"
            chkOpen.AutoSize = True
            SpanRow(t, chkOpen)
            btnBuild.Text = "Build save editor"
            btnBuild.AutoSize = True
            btnBuild.Padding = New Padding(14, 5, 14, 5)
            btnBuild.Font = New Font(Font, FontStyle.Bold)
            AddHandler btnBuild.Click, AddressOf DoBuild
            Dim f As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill}
            f.Controls.Add(btnBuild)
            f.Controls.Add(Btn("Restamp version only", AddressOf DoRestamp))
            SpanRow(t, f)
            SetupLog(txtBuildLog)
            page.Controls.Add(txtBuildLog)
            page.Controls.Add(t)
            Return page
        End Function

        Private Sub PickOut(sender As Object, e As EventArgs)
            Using d As New FolderBrowserDialog With {.SelectedPath = txtOut.Text}
                If d.ShowDialog(Me) = DialogResult.OK Then txtOut.Text = d.SelectedPath : UpdateTemplateLabel()
            End Using
        End Sub

        Private Sub OpenOut(sender As Object, e As EventArgs)
            Try
                Directory.CreateDirectory(txtOut.Text)
                Process.Start(New ProcessStartInfo(txtOut.Text) With {.UseShellExecute = True})
            Catch ex As Exception
                MessageBox.Show(Me, ex.Message, Text)
            End Try
        End Sub

        Private Sub DoBuild(sender As Object, e As EventArgs)
            grid.EndEdit()
            If template Is Nothing Then
                MessageBox.Show(Me, "Scan the game first (tab 1).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If
            If keyResult IsNot Nothing AndAlso keyResult.Changed AndAlso Not keyResult.Verified AndAlso
               allFindings.Any(Function(f) f.Category = Cat.Key AndAlso f.Include) Then
                If MessageBox.Show(Me, "The new seal key could not be confirmed with a save. If it is wrong, the editor will not open saves. Use it anyway?",
                                   Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
            End If
            Try
                ' rebuild from the original page every time, so building twice does not add things twice
                Dim t = New EditorTemplate(template.Html)
                Dim msgs = EditorBuilder.Apply(t, allFindings, keyResult)
                Directory.CreateDirectory(txtOut.Text)
                Dim outFile = Path.Combine(txtOut.Text, "index.html")
                If File.Exists(outFile) Then
                    File.Copy(outFile, Path.Combine(txtOut.Text, "index.previous.html"), True)
                End If
                t.Save(outFile)
                txtBuildLog.Clear()
                For Each m In msgs : txtBuildLog.AppendText(m & Environment.NewLine) : Next
                txtBuildLog.AppendText(Environment.NewLine & "Saved " & outFile & Environment.NewLine)
                txtBuildLog.AppendText("The previous version (if any) was kept as index.previous.html." & Environment.NewLine)
                UpdateTemplateLabel()
                If chkOpen.Checked Then Process.Start(New ProcessStartInfo(outFile) With {.UseShellExecute = True})
            Catch ex As Exception
                MessageBox.Show(Me, "Build failed:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Sub

        Private Sub DoRestamp(sender As Object, e As EventArgs)
            Try
                Dim t = LoadTemplate()
                t.StampVersion()
                Directory.CreateDirectory(txtOut.Text)
                Dim outFile = Path.Combine(txtOut.Text, "index.html")
                t.Save(outFile)
                txtBuildLog.AppendText(t.VersionLabel() & " -> " & outFile & Environment.NewLine)
                UpdateTemplateLabel()
            Catch ex As Exception
                MessageBox.Show(Me, ex.Message, Text)
            End Try
        End Sub

        ' ============================== 4. save file ==============================

        Private Function BuildSaveTab() As TabPage
            Dim page As New TabPage("Save file (raw)")
            Dim top As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(6)}
            top.Controls.Add(Btn("Open save...", AddressOf OpenSave))
            top.Controls.Add(Btn("Save sealed copy...", AddressOf SaveSealed))
            lblSaveState.AutoSize = True
            lblSaveState.Margin = New Padding(12, 9, 4, 4)
            top.Controls.Add(lblSaveState)
            Dim hintLbl = Hint("Opens a save with the key of the editor being updated (or the new key found by the scan), shows it as text, " &
                            "and seals your edits again. For full editing use the built editor page.")
            hintLbl.Dock = DockStyle.Top
            hintLbl.Padding = New Padding(8, 0, 8, 4)
            txtSaveText.Multiline = True
            txtSaveText.ScrollBars = ScrollBars.Both
            txtSaveText.WordWrap = False
            txtSaveText.AcceptsReturn = True
            txtSaveText.Dock = DockStyle.Fill
            txtSaveText.Font = New Font("Consolas", 10.0F)
            page.Controls.Add(txtSaveText)
            page.Controls.Add(hintLbl)
            page.Controls.Add(top)
            Return page
        End Function

        Private Function CurrentSeal() As SaveSeal
            If keyResult IsNot Nothing AndAlso keyResult.Seal IsNot Nothing Then Return keyResult.Seal
            Return If(template, LoadTemplate()).GetSeal()
        End Function

        Private Sub OpenSave(sender As Object, e As EventArgs)
            Using d As New OpenFileDialog With {.Filter = "Azeroth Survivors save|*.sav;*.txt|All files|*.*"}
                If d.ShowDialog(Me) <> DialogResult.OK Then Return
                Try
                    Dim ok = False
                    Dim plain = CurrentSeal().Unseal(File.ReadAllText(d.FileName), ok)
                    txtSaveText.Text = plain.Replace(vbLf, vbCrLf)
                    lblSaveState.Text = Path.GetFileName(d.FileName) & If(ok, " - seal OK", " - seal does NOT match (edited, damaged, or a different key)")
                    lblSaveState.ForeColor = If(ok, Color.DarkGreen, Color.DarkRed)
                Catch ex As Exception
                    MessageBox.Show(Me, "Could not open this save:" & vbCrLf & ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                End Try
            End Using
        End Sub

        Private Sub SaveSealed(sender As Object, e As EventArgs)
            If txtSaveText.TextLength = 0 Then Return
            Dim stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")
            Using d As New SaveFileDialog With {.Filter = "Azeroth Survivors save|*.sav|All files|*.*", .FileName = "AzerothSurvivors_edited_" & stamp & ".sav"}
                If d.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim plain = txtSaveText.Text.Replace(vbCrLf, vbLf)
                File.WriteAllText(d.FileName, CurrentSeal().Seal(plain), New UTF8Encoding(False))
                lblSaveState.Text = "Saved " & Path.GetFileName(d.FileName)
                lblSaveState.ForeColor = Color.DarkGreen
            End Using
        End Sub
    End Class

End Namespace
