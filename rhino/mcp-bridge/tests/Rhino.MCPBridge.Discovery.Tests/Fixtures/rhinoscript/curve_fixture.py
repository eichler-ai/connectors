import rhinoscriptsyntax_internal as _x

def AddCircle(plane_or_center,
              radius):
    """Adds a circle curve to the document
    Parameters:
      plane_or_center (point|plane): plane on which the circle will lie
      radius (number): the radius of the circle
    Returns:
      guid: id of the new curve object
    Example:
      import rhinoscriptsyntax as rs
      rs.AddCircle(rs.WorldXYPlane(), 5.0)
    See Also:
      IsCircle
    """
    return _x.add(plane_or_center, radius)

def AddLine(start, end):
    """Adds a line curve to the current model"""
    return _x.add(start, end)

def coercecurve(id, segment_index=-1):
    """A lowercase helper that must not be indexed
    Returns:
      Curve: the coerced curve
    """
    return None

def __privatehelper(x):
    """Dunder helper, excluded."""
    return x
