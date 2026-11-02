Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Core

    Public Enum FindStatus
        [New]
        Changed
        Missing
        Info
    End Enum

    ''' <summary>One row in the review grid.</summary>
    Public Class Finding
        Public Property Include As Boolean = True
        Public Property Category As String = ""
        Public Property Status As FindStatus = FindStatus.New
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Description As String = ""
        Public Property Icon As String = ""
        ''' <summary>Achievement tab, perk max rank, codex rarity, talent "tree|max|row|col", depending on the category.</summary>
        Public Property Extra As String = ""
        Public Property Notes As String = ""
        Public Property Offset As Long = -1
        ''' <summary>For Changed rows: which value changed (icon, name, max, rarity, talent, gear).</summary>
        Public Property Prop As String = ""
        ''' <summary>Where the value lives (gear: "s/warrior/5"; talent: class id).</summary>
        Public Property Path As String = ""
        ''' <summary>Extra data that does not fit a column (gear icons: item id -> icon).</summary>
        Public Property Data As Dictionary(Of String, String)
    End Class

    ''' <summary>Category names used in the grid and by the builder.</summary>
    Public Module Cat
        Public Const Achievement = "Achievement"
        Public Const Zone = "Zone"
        Public Const Pet = "Pet"
        Public Const Tabard = "Tabard"
        Public Const Perk = "Perk"
        Public Const Jewelry = "Codex: rings & trinkets"
        Public Const Armory = "Codex: armory"
        Public Const Talent = "Talent"
        Public Const Ability = "Ability"
        Public Const Gear = "Gear"
        Public Const GearItem = "Gear item id (info)"
        Public Const Key = "Save seal key"

        ''' <summary>Categories whose rows can be written into the editor.</summary>
        Public ReadOnly Writable As String() = {Achievement, Zone, Pet, Tabard, Perk, Jewelry, Armory, Talent, Ability, Gear, Key}
    End Module

    Public Structure TalentInfo
        Public Id As String, Max As Integer, Row As Integer, Col As Integer
        ''' <summary>Index into the TREES list (class position * 3 + tree).</summary>
        Public Tree As Integer
    End Structure

    ''' <summary>The ids the current editor already knows, read from its data tables.</summary>
    Public Class KnownData
        Public ReadOnly Ids As New Dictionary(Of String, HashSet(Of String))
        ''' <summary>Achievement id -> (title, tab) for neighbour lookups.</summary>
        Public ReadOnly AchInfo As New Dictionary(Of String, (Title As String, Tab As String))
        Public ReadOnly ZoneOrder As New List(Of String)
        Public ReadOnly ArmoryRarity As New Dictionary(Of String, Integer)
        Public ReadOnly PerkMax As New Dictionary(Of String, Integer)
        Public ReadOnly AllIds As New HashSet(Of String)
        ''' <summary>id -> display name, where the editor has one (used to learn how the game lays out its tables).</summary>
        Public ReadOnly Names As New Dictionary(Of String, String)
        Public ReadOnly Descs As New Dictionary(Of String, String)
        ''' <summary>category -> id -> property -> value, everything the editor knows about an entry (icon, name, max...).</summary>
        Public ReadOnly Props As New Dictionary(Of String, Dictionary(Of String, Dictionary(Of String, String)))
        Public ReadOnly Talents As New Dictionary(Of String, TalentInfo)
        Public ReadOnly TreeCount As New List(Of Integer)
        Public ReadOnly ClsOrder As New List(Of Integer)
        Public Gear As JsonObject
        Public Seal As SaveSeal

        Public Shared ReadOnly ClassNames As New Dictionary(Of Integer, String) From {
            {1, "Warrior"}, {2, "Paladin"}, {3, "Hunter"}, {4, "Rogue"}, {5, "Priest"}, {6, "Death Knight"}, {7, "Shaman"}, {8, "Mage"}, {9, "Warlock"}, {11, "Druid"}}
        Public Shared ReadOnly TreeNames As New Dictionary(Of Integer, String()) From {
            {1, {"Arms", "Fury", "Protection"}}, {2, {"Holy", "Protection", "Retribution"}}, {3, {"Beast Mastery", "Marksmanship", "Survival"}},
            {4, {"Assassination", "Combat", "Subtlety"}}, {5, {"Discipline", "Holy", "Shadow"}}, {7, {"Elemental", "Enhancement", "Restoration"}},
            {8, {"Arcane", "Fire", "Frost"}}, {9, {"Affliction", "Demonology", "Destruction"}}, {11, {"Balance", "Feral Combat", "Restoration"}}}

        Public Function IdsOf(category As String) As HashSet(Of String)
            Dim s As HashSet(Of String) = Nothing
            If Not Ids.TryGetValue(category, s) Then s = New HashSet(Of String) : Ids(category) = s
            Return s
        End Function

        Public Sub SetProp(category As String, id As String, prop As String, value As String)
            If String.IsNullOrEmpty(value) Then Return
            Dim c As Dictionary(Of String, Dictionary(Of String, String)) = Nothing
            If Not Props.TryGetValue(category, c) Then c = New Dictionary(Of String, Dictionary(Of String, String)) : Props(category) = c
            Dim e As Dictionary(Of String, String) = Nothing
            If Not c.TryGetValue(id, e) Then e = New Dictionary(Of String, String) : c(id) = e
            e(prop) = value
        End Sub

        Public Function GetProp(category As String, id As String, prop As String) As String
            Dim c As Dictionary(Of String, Dictionary(Of String, String)) = Nothing
            Dim e As Dictionary(Of String, String) = Nothing
            Dim v As String = Nothing
            If Props.TryGetValue(category, c) AndAlso c.TryGetValue(id, e) AndAlso e.TryGetValue(prop, v) Then Return v
            Return Nothing
        End Function

        ''' <summary>Class id and tree name of a TREES index.</summary>
        Public Function TreeLabel(tree As Integer) As String
            Dim ci = tree \ 3, ti = tree Mod 3
            If ci >= ClsOrder.Count Then Return "Tree " & (tree + 1)
            Dim c = ClsOrder(ci)
            Dim tn As String() = Nothing
            Return ClassNames.GetValueOrDefault(c, "Class " & c) & " " & If(TreeNames.TryGetValue(c, tn), tn(ti), "Tree " & (ti + 1))
        End Function

        ''' <summary>Parses "Warrior Arms" (or a plain number) back into a TREES index; -1 if unknown.</summary>
        Public Function TreeIndex(label As String) As Integer
            Dim n As Integer
            If Integer.TryParse(label, n) Then Return n
            For i = 0 To ClsOrder.Count * 3 - 1
                If String.Equals(TreeLabel(i), label.Trim(), StringComparison.OrdinalIgnoreCase) Then Return i
            Next
            Return -1
        End Function

        Private Shared Function Str(n As JsonNode) As String
            If n Is Nothing Then Return Nothing
            Return If(n.GetValueKind() = JsonValueKind.String, n.GetValue(Of String)(), n.ToJsonString())
        End Function

        Public Shared Function FromTemplate(t As EditorTemplate) As KnownData
            Dim k As New KnownData()
            k.Seal = t.GetSeal()

            Dim co = TryCast(t.GetJson("CLS_ORDER"), JsonArray)
            If co IsNot Nothing Then
                For Each c In co : k.ClsOrder.Add(c.GetValue(Of Integer)()) : Next
            Else
                k.ClsOrder.AddRange({1, 2, 3, 4, 5, 7, 8, 9, 11})
            End If

            Dim zones = TryCast(t.GetJson("ZONES"), JsonArray)
            If zones IsNot Nothing Then
                For Each z In zones : k.ZoneOrder.Add(z.GetValue(Of String)()) : k.IdsOf(Cat.Zone).Add(z.GetValue(Of String)()) : Next
            End If

            Dim zn = TryCast(t.GetJson("ZONE_NAMES"), JsonObject)
            If zn IsNot Nothing Then
                For Each p In zn : k.Names(p.Key) = p.Value.GetValue(Of String)() : Next
            End If

            Dim achs = TryCast(t.GetJson("ACHS"), JsonArray)
            If achs IsNot Nothing Then
                For Each a In achs
                    Dim arr = a.AsArray()
                    Dim id = arr(0).GetValue(Of String)()
                    k.IdsOf(Cat.Achievement).Add(id)
                    k.AchInfo(id) = (arr(1).GetValue(Of String)(), If(arr.Count > 4, arr(4).GetValue(Of String)(), "Feats"))
                    k.Names(id) = arr(1).GetValue(Of String)()
                    k.Descs(id) = arr(2).GetValue(Of String)()
                    k.SetProp(Cat.Achievement, id, "name", arr(1).GetValue(Of String)())
                    k.SetProp(Cat.Achievement, id, "desc", arr(2).GetValue(Of String)())
                    ' class achievements use the class icon, which is not a WoW icon name
                    If arr.Count > 3 AndAlso Not id.StartsWith("class") Then k.SetProp(Cat.Achievement, id, "icon", arr(3).GetValue(Of String)())
                Next
            End If

            AddKeys(k, Cat.Pet, t.GetJson("PETS"))
            AddKeys(k, Cat.Tabard, t.GetJson("TABARDS"))
            Dim perks = TryCast(t.GetJson("PERKMAX"), JsonObject)
            If perks IsNot Nothing Then
                For Each p In perks
                    k.IdsOf(Cat.Perk).Add(p.Key)
                    k.PerkMax(p.Key) = p.Value.GetValue(Of Integer)()
                    k.SetProp(Cat.Perk, p.Key, "max", p.Value.GetValue(Of Integer)().ToString())
                Next
            End If

            Dim codex = TryCast(t.GetJson("CODEX"), JsonObject)
            If codex IsNot Nothing Then
                AddKeys(k, Cat.Jewelry, codex("J"))
                Dim a = TryCast(codex("A"), JsonObject)
                If a IsNot Nothing Then
                    For Each p In a
                        k.IdsOf(Cat.Armory).Add(p.Key)
                        Dim r = 0
                        Try : r = p.Value.AsArray()(1).GetValue(Of Integer)() : Catch : End Try
                        k.ArmoryRarity(p.Key) = r
                        k.SetProp(Cat.Armory, p.Key, "rarity", r.ToString())
                        Try
                            k.Names(p.Key) = p.Value.AsArray()(0).GetValue(Of String)()
                            k.SetProp(Cat.Armory, p.Key, "name", k.Names(p.Key))
                        Catch
                        End Try
                    Next
                End If
                ' legendary abilities: known ids only (so they are not reported as "new")
                Dim l = TryCast(codex("L"), JsonObject)
                If l IsNot Nothing Then
                    For Each p In l
                        For Each x In p.Value.AsArray() : k.AllIds.Add(x.GetValue(Of String)()) : Next
                    Next
                End If
            End If

            ' talents: TREES is a string "id:max:row:col,...|..." with 3 trees per class in CLS_ORDER order
            Dim trees = t.GetJson("TREES")
            If trees IsNot Nothing Then
                Dim tr = trees.GetValue(Of String)().Split("|"c)
                For ti = 0 To tr.Length - 1
                    Dim n = 0
                    For Each part In tr(ti).Split(","c, StringSplitOptions.RemoveEmptyEntries)
                        Dim f = part.Split(":"c)
                        If f.Length < 4 Then Continue For
                        Dim info As New TalentInfo With {.Id = f(0), .Max = CInt(f(1)), .Row = CInt(f(2)), .Col = CInt(f(3)), .Tree = ti}
                        k.Talents(f(0)) = info
                        k.IdsOf(Cat.Talent).Add(f(0))
                        k.SetProp(Cat.Talent, f(0), "max", f(1))
                        k.SetProp(Cat.Talent, f(0), "row", f(2))
                        k.SetProp(Cat.Talent, f(0), "col", f(3))
                        k.SetProp(Cat.Talent, f(0), "tree", (ti Mod 3).ToString())
                        n += 1
                    Next
                    k.TreeCount.Add(n)
                Next
            End If

            ' icon tables: IC.t / IC.p / IC.ab = [icon, name]; IC.j / IC.a / IC.pet / IC.tab = icon
            Dim ic = TryCast(t.GetJson("IC"), JsonObject)
            If ic IsNot Nothing Then
                For Each grp In ic
                    Dim o = TryCast(grp.Value, JsonObject)
                    If o Is Nothing Then Continue For
                    Dim category = IconGroupCategory(grp.Key)
                    For Each p In o
                        k.AllIds.Add(p.Key)
                        Dim a = TryCast(p.Value, JsonArray)
                        If a IsNot Nothing AndAlso a.Count > 1 Then
                            If Not k.Names.ContainsKey(p.Key) Then k.Names(p.Key) = Str(a(1))
                            If category IsNot Nothing Then
                                k.SetProp(category, p.Key, "icon", Str(a(0)))
                                k.SetProp(category, p.Key, "name", Str(a(1)))
                            End If
                        ElseIf category IsNot Nothing AndAlso p.Value IsNot Nothing Then
                            k.SetProp(category, p.Key, "icon", Str(p.Value))
                        End If
                    Next
                Next
            End If
            Dim ab = TryCast(t.GetJson("IC.ab"), JsonObject)
            If ab IsNot Nothing Then
                For Each p In ab
                    k.AllIds.Add(p.Key)
                    k.IdsOf(Cat.Ability).Add(p.Key)
                    Dim a = TryCast(p.Value, JsonArray)
                    If a IsNot Nothing AndAlso a.Count > 1 Then
                        k.SetProp(Cat.Ability, p.Key, "icon", Str(a(0)))
                        k.SetProp(Cat.Ability, p.Key, "name", Str(a(1)))
                        If Not k.Names.ContainsKey(p.Key) Then k.Names(p.Key) = Str(a(1))
                    End If
                Next
            End If
            Dim tabIc = TryCast(t.GetJson("IC.tab"), JsonObject)
            If tabIc IsNot Nothing Then
                For Each p In tabIc : k.SetProp(Cat.Tabard, p.Key, "icon", Str(p.Value)) : Next
            End If

            Dim cosm = TryCast(t.GetJson("COSM"), JsonArray)
            If cosm IsNot Nothing Then
                For Each c In cosm : k.AllIds.Add(c.AsArray()(0).ToString()) : Next
            End If

            k.Gear = TryCast(t.GetJson("GEAR"), JsonObject)

            For Each s In k.Ids.Values
                For Each x In s : k.AllIds.Add(x) : Next
            Next
            Return k
        End Function

        Public Shared Function IconGroupCategory(group As String) As String
            Select Case group
                Case "t" : Return Cat.Talent
                Case "p" : Return Cat.Perk
                Case "j" : Return Cat.Jewelry
                Case "a" : Return Cat.Armory
                Case "pet" : Return Cat.Pet
                Case "tab" : Return Cat.Tabard
                Case "ab" : Return Cat.Ability
                Case Else : Return Nothing
            End Select
        End Function

        Private Shared Sub AddKeys(k As KnownData, category As String, node As JsonNode)
            Dim o = TryCast(node, JsonObject)
            If o Is Nothing Then Return
            For Each p In o
                k.IdsOf(category).Add(p.Key)
                If p.Value IsNot Nothing AndAlso p.Value.GetValueKind() = JsonValueKind.String Then
                    k.Names(p.Key) = p.Value.GetValue(Of String)()
                    k.SetProp(category, p.Key, "name", p.Value.GetValue(Of String)())
                End If
            Next
        End Sub
    End Class

End Namespace
