Imports System.IO
Imports System.Text.Json.Nodes

Namespace Core

    Public Enum FindStatus
        [New]
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
        ''' <summary>Achievement tab, perk max rank or codex rarity, depending on the category.</summary>
        Public Property Extra As String = ""
        Public Property Notes As String = ""
        Public Property Offset As Long = -1
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
        Public Const Talent = "Talent (info only)"
        Public Const Key = "Save seal key"

        ''' <summary>Categories whose "New" rows can be written into the editor.</summary>
        Public ReadOnly Writable As String() = {Achievement, Zone, Pet, Tabard, Perk, Jewelry, Armory, Key}
    End Module

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
        Public Seal As SaveSeal

        Public Function IdsOf(category As String) As HashSet(Of String)
            Dim s As HashSet(Of String) = Nothing
            If Not Ids.TryGetValue(category, s) Then s = New HashSet(Of String) : Ids(category) = s
            Return s
        End Function

        Public Shared Function FromTemplate(t As EditorTemplate) As KnownData
            Dim k As New KnownData()
            k.Seal = t.GetSeal()

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
                Next
            End If

            AddKeys(k, Cat.Pet, t.GetJson("PETS"))
            AddKeys(k, Cat.Tabard, t.GetJson("TABARDS"))
            Dim perks = TryCast(t.GetJson("PERKMAX"), JsonObject)
            If perks IsNot Nothing Then
                For Each p In perks : k.IdsOf(Cat.Perk).Add(p.Key) : k.PerkMax(p.Key) = p.Value.GetValue(Of Integer)() : Next
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
                        Try : k.Names(p.Key) = p.Value.AsArray()(0).GetValue(Of String)() : Catch : End Try
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

            ' talents: TREES is a string "id:max:row:col,...|..."
            Dim trees = t.GetJson("TREES")
            If trees IsNot Nothing Then
                For Each part In trees.GetValue(Of String)().Split({"|"c, ","c}, StringSplitOptions.RemoveEmptyEntries)
                    k.IdsOf(Cat.Talent).Add(part.Split(":"c)(0))
                Next
            End If

            ' everything else the editor mentions (icons tables, abilities...) also counts as "known"
            Dim ic = TryCast(t.GetJson("IC"), JsonObject)
            If ic IsNot Nothing Then
                For Each grp In ic
                    Dim o = TryCast(grp.Value, JsonObject)
                    If o IsNot Nothing Then
                        For Each p In o
                            k.AllIds.Add(p.Key)
                            Dim a = TryCast(p.Value, JsonArray)
                            If a IsNot Nothing AndAlso a.Count > 1 AndAlso Not k.Names.ContainsKey(p.Key) Then
                                Try : k.Names(p.Key) = a(1).GetValue(Of String)() : Catch : End Try
                            End If
                        Next
                    End If
                Next
            End If
            Dim ab = TryCast(t.GetJson("IC.ab"), JsonObject)
            If ab IsNot Nothing Then
                For Each p In ab : k.AllIds.Add(p.Key) : Next
            End If

            Dim cosm = TryCast(t.GetJson("COSM"), JsonArray)
            If cosm IsNot Nothing Then
                For Each c In cosm : k.AllIds.Add(c.AsArray()(0).ToString()) : Next
            End If

            For Each s In k.Ids.Values
                For Each x In s : k.AllIds.Add(x) : Next
            Next
            Return k
        End Function

        Private Shared Sub AddKeys(k As KnownData, category As String, node As JsonNode)
            Dim o = TryCast(node, JsonObject)
            If o Is Nothing Then Return
            For Each p In o
                k.IdsOf(category).Add(p.Key)
                If p.Value IsNot Nothing AndAlso p.Value.GetValueKind() = System.Text.Json.JsonValueKind.String Then k.Names(p.Key) = p.Value.GetValue(Of String)()
            Next
        End Sub
    End Class

End Namespace
