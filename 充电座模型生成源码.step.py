from build123d import Align, Box, Cylinder, Location, Rot

def gen_step():
    # Trial coupon only: reproduces holder dimensions, not the entire enclosure.
    base = Box(32, 34, 2, align=(Align.CENTER, Align.CENTER, Align.MIN))
    for x in (-7.3, 7.3):
        base += Location((x, -1.5, 2)) * Box(2.4, 22, 12, align=(Align.CENTER, Align.CENTER, Align.MIN))
    for x in (-6.3, 6.3):
        base += Location((x, -11, 2)) * Box(4.4, 3, 12, align=(Align.CENTER, Align.CENTER, Align.MIN))
    base += Location((0, 10.75, 2)) * Box(32, 2.5, 12, align=(Align.CENTER, Align.CENTER, Align.MIN))
    base -= Location((0, 13, 8)) * Rot(X=90) * Cylinder(7.5, 5, align=(Align.CENTER, Align.CENTER, Align.MIN))
    for x in (-6.875, 6.875):
        base += Location((x, 10.25, 2)) * Box(3.25, 1.5, 12, align=(Align.CENTER, Align.CENTER, Align.MIN))
    base.label = 'Temporary charge holder trial coupon - not an installed component'
    return base
