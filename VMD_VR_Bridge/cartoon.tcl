# NewCartoon metadata for coordinate playback. Sourced by trajectory.tcl.
#
# cartoon_metadata describes one protein NewCartoon representation for the "cartoons" array of
# the TRAJ_OPEN reply: the backbone CA atoms split into continuous chains, with secondary
# structure and colors. Unity (Assets/Scripts/Trajectory/VMDCartoonScene.cs) builds the ribbon
# from these atoms' coordinates.
# Only metadata is produced here. Playback uses the existing bounded coordinate stream
# (TRAJ_READ).
# Keep the VDW/CPK metadata (trajectory.tcl) and the QuickSurf metadata (quicksurf.tcl)
# independent of this module.

# Return the VMD selection text for the backbone atoms a cartoon is built from: the CA atoms
# of residues that VMD classifies as protein or whose residue name is in the list below.
proc ::vrbridge::cartoon_backbone {} {
    # Include CA-only protein models, without interpreting calcium ions as a backbone.
    return {name CA and (protein or resname ALA ARG ASN ASP CYS GLN GLU GLY HIS ILE LEU LYS MET PHE PRO SER THR TRP TYR VAL HSD HSE HSP HID HIE HIP MSE)}
}

# Return the secondary-structure codes of the backbone atoms of molecule m.
# trajectory_signature includes them for visible NewCartoon reps, so a change of VMD's
# structure assignments invalidates the snapshot.
proc ::vrbridge::cartoon_structure_signature {m} {
    set sel [atomselect $m [::vrbridge::cartoon_backbone]]
    set code [catch { ::vrbridge::cartoon_structures $sel } result options]
    $sel delete
    if {$code} { return -options $options $result }
    return $result
}

# Return one secondary-structure code per atom of selection sel (the letters listed in
# cartoon_color). Called by cartoon_structure_signature and cartoon_metadata.
proc ::vrbridge::cartoon_structures {sel} {
    # STRIDE cannot classify a CA-only molecule. Do not invoke it just to draw a trace.
    # When no atom of the selection is flagged as protein, every atom is reported as coil (C).
    if {[lsearch -exact [$sel get protein] 1] < 0} { return [lrepeat [$sel num] C] }
    return [$sel get structure]
}

# Return the r g b color of one backbone atom.
# coloring: the rep's coloring string. row: the atom's fields, in the order given below.
# ColorID, Name, Element, ResName, Chain, SegName and Structure are supported; any other
# method raises an error, which makes TRAJ_OPEN report the scene as unsupported.
proc ::vrbridge::cartoon_color {coloring row} {
    # row: index residue chain segname pfrag protein structure name element resname
    set category [lindex $coloring 0]
    switch -- $category {
        ColorID { return [colorinfo rgb [lindex $coloring 1]] }
        Name { set value [string index [lindex $row 7] 0] }
        Element { set value [lindex $row 8] }
        ResName { set category Resname; set value [lindex $row 9] }
        Chain { set value [lindex $row 2] }
        SegName { set category Segname; set value [lindex $row 3] }
        Structure {
            # Map the structure code to its entry in VMD's Structure color category; a code
            # that is not listed is colored as Coil.
            set names [dict create H {Alpha Helix} G 3_10_Helix I Pi_Helix E Extended_Beta B Bridge_Beta T Turn C Coil]
            set code [lindex $row 6]
            if {![dict exists $names $code]} { set code C }
            set value [dict get $names $code]
        }
        default { error "NewCartoon color method $category uses the existing renderer" }
    }
    return [colorinfo rgb [color $category $value]]
}

# Format one continuous chain as a JSON object, one entry per residue.
# indices: the CA atom indices. carbons, oxygens: the atom index of the residue's C and O
# atom, or -1. structures: 0 for coil, 1 for helix, 2 for sheet. colors: a flat r g b list,
# three values per residue.
proc ::vrbridge::cartoon_chain_json {indices carbons oxygens structures colors} {
    return [format {{"indices":[%s],"carbons":[%s],"oxygens":[%s],"structures":[%s],"colors":[%s]}} \
        [join $indices ,] [join $carbons ,] [join $oxygens ,] [join $structures ,] [join $colors ,]]
}

# Return the JSON object for one NewCartoon rep: id (the rep index), radius, resolution,
# aspect and spline (the four NewCartoon style parameters) and chains.
# m: the molecule ID. selection, coloring, style: the rep's VMD strings. repIndex: its index.
# A chain is a run of consecutive backbone residues that are all selected and share the same
# chain, segname and, for residues VMD flags as protein, protein fragment (pfrag).
# Raises an error when the selection contains nucleic acid or uses an unsupported coloring.
# Called by trajectory_metadata in trajectory.tcl.
proc ::vrbridge::cartoon_metadata {m selection coloring style repIndex} {
    set selected [atomselect $m $selection]
    set backbone ""
    set planes ""
    set nucleic ""
    set code [catch {
        # Nucleic cartoons need their own base geometry; never silently omit them.
        set nucleic [atomselect $m "($selection) and nucleic"]
        if {[$nucleic num] > 0} { error "Nucleic NewCartoon uses the existing renderer" }
        set chosen [dict create]
        foreach index [$selected get index] { dict set chosen $index 1 }
        set backbone [atomselect $m [::vrbridge::cartoon_backbone]]
        # One row per backbone atom of the whole molecule, with its structure code inserted
        # at position 6 (the layout that cartoon_color documents).
        set rows {}
        foreach row [$backbone get {index residue chain segname pfrag protein name element resname}] structure [::vrbridge::cartoon_structures $backbone] {
            lappend rows [linsert $row 6 $structure]
        }
        # The first C and the first O atom of each residue. Unity takes the ribbon's
        # orientation from the C-to-O direction; a residue without them gets -1.
        set planes [atomselect $m {protein and name C O}]
        set planeAtoms [dict create]
        foreach row [$planes get {index residue name}] {
            lassign $row index residue name
            if {![dict exists $planeAtoms $residue $name]} { dict set planeAtoms $residue $name $index }
        }
        set chains {}; set indices {}; set carbons {}; set oxygens {}; set structures {}; set colors {}
        set seen [dict create]; set lastKey ""; set gap 1
        foreach row $rows {
            lassign $row index residue chain segment fragment protein structure
            # Only the first backbone atom of a residue is used.
            if {[dict exists $seen $residue]} { continue }
            dict set seen $residue 1
            # Never connect across a hidden residue, chain/segment boundary or protein fragment.
            set key [list $chain $segment [expr {$protein ? $fragment : -1}]]
            if {![dict exists $chosen $index]} { set gap 1; continue }
            if {($gap || $key ne $lastKey) && [llength $indices] > 0} {
                lappend chains [::vrbridge::cartoon_chain_json $indices $carbons $oxygens $structures $colors]
                set indices {}; set carbons {}; set oxygens {}; set structures {}; set colors {}
            }
            lappend indices $index
            foreach {name output} {C carbons O oxygens} {
                set atom -1
                if {[dict exists $planeAtoms $residue $name]} { set atom [dict get $planeAtoms $residue $name] }
                lappend $output $atom
            }
            # 1 for the helix codes, 2 for the sheet codes, 0 for everything else.
            set kind 0
            if {$structure in {H G I}} { set kind 1 }
            if {$structure in {E B}} { set kind 2 }
            lappend structures $kind
            lappend colors {*}[::vrbridge::cartoon_color $coloring $row]
            set gap 0; set lastKey $key
        }
        if {[llength $indices]} { lappend chains [::vrbridge::cartoon_chain_json $indices $carbons $oxygens $structures $colors] }
        set radius [lindex $style 1]
        set resolution [lindex $style 2]
        set aspect [lindex $style 3]
        set spline [lindex $style 4]
        # VMD retains a bare "NewCartoon" command string when defaults were used.
        # The values below are the ones scene_set_representation applies for NewCartoon.
        if {$radius eq ""} { set radius 0.3 }
        if {$resolution eq ""} { set resolution 10 }
        if {$aspect eq ""} { set aspect 4.1 }
        if {$spline eq ""} { set spline 0 }
        set resolution [expr {int($resolution)}]
        set result [format {{"id":%d,"radius":%s,"resolution":%d,"aspect":%s,"spline":%d,"chains":[%s]}} \
            $repIndex $radius $resolution $aspect $spline [join $chains ,]]
    } result options]
    foreach sel [list $selected $backbone $planes $nucleic] { if {$sel ne ""} { $sel delete } }
    if {$code} { return -options $options $result }
    return $result
}
