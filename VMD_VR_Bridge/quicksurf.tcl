# QuickSurf metadata only. Unity rebuilds the Gaussian density surface from every
# coordinate frame on the GPU; VMD never renders or exports QuickSurf geometry here.
proc ::vrbridge::quicksurf_metadata {m selection coloring style repIndex} {
    # VMD keeps a bare "QuickSurf" (or "Quicksurf") string when defaults were used.
    lassign [lrange $style 1 4] radiusScale isovalue spacing quality
    if {$radiusScale eq ""} { set radiusScale 1.0 }
    if {$isovalue eq ""} { set isovalue 0.5 }
    if {$spacing eq ""} { set spacing 1.0 }
    if {$quality eq ""} { set quality 0 }
    foreach value [list $radiusScale $isovalue $spacing $quality] {
        if {![string is double -strict $value]} { error "Invalid QuickSurf parameters: $style" }
    }
    if {$radiusScale <= 0 || $isovalue <= 0 || $spacing <= 0} { error "Invalid QuickSurf parameters: $style" }
    set quality [expr {max(0, min(3, int(round($quality))))}]
    set sel [atomselect $m $selection]
    set code [catch {
        set colors [::vrbridge::trajectory_atom_colors $sel $coloring]
        set result [format {{"id":%d,"indices":[%s],"radii":[%s],"colors":[%s],"radiusScale":%s,"isovalue":%s,"gridSpacing":%s,"quality":%d}} \
            $repIndex [join [$sel get index] ,] [join [$sel get radius] ,] [join $colors ,] \
            $radiusScale $isovalue $spacing $quality]
    } message options]
    $sel delete
    if {$code} { return -options $options $message }
    return $result
}
