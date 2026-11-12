# QuickSurf metadata for coordinate playback. Sourced by trajectory.tcl.
# Only metadata is produced here. Unity rebuilds the Gaussian density surface from every
# coordinate frame on the GPU; VMD never renders or exports QuickSurf geometry here.

# Return the JSON object for one QuickSurf rep in the "quicksurfs" array of the TRAJ_OPEN
# reply: id (the rep index), the indices, radii and colors (flat r g b) of the selected
# atoms, and the style parameters radiusScale, isovalue, gridSpacing and quality.
# m: the molecule ID. selection, coloring, style: the rep's VMD strings. repIndex: its index.
# Missing style parameters get the defaults below; quality is rounded and limited to 0..3.
# Raises an error for a non-numeric parameter, for a radius scale, isovalue or grid spacing
# that is not positive, and for an unsupported coloring (see trajectory_atom_colors).
# Called by trajectory_metadata in trajectory.tcl.
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
