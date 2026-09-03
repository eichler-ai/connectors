''' <summary>
''' Stands in for Autodesk.Revit.DB.FootPrintRoof: two named indexed properties (one read/write, one
''' read-only), one genuine default indexer, and one plain property, so the tests can tell the three
''' property shapes apart on a single type.
''' </summary>
Public Class NamedIndexedFixture

    ''' <summary>Retrieve or set the slope angle of the curve.</summary>
    ''' <param name="curve">The footprint curve.</param>
    Public Property SlopeAngle(curve As Integer) As Double
        Get
            Return curve
        End Get
        Set(value As Double)
        End Set
    End Property

    ''' <summary>Retrieve the overhang of the curve.</summary>
    ''' <param name="curve">The footprint curve.</param>
    Public ReadOnly Property Overhang(curve As Integer) As Double
        Get
            Return curve
        End Get
    End Property

    ''' <summary>The default indexer, which C# reaches as obj[i].</summary>
    ''' <param name="i">The slot.</param>
    Default Public Property Item(i As Integer) As Integer
        Get
            Return i
        End Get
        Set(value As Integer)
        End Set
    End Property

    ''' <summary>An ordinary property, to pin that accessor aliasing stays limited to indexed ones.</summary>
    Public Property Plain As Integer

End Class
