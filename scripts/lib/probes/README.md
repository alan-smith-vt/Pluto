# Probes for the STAAD `.anl` reader

`anl-prints-probe.std` is a small made-up frame (32 members, 18 joints, 6 primary cases, 8 combinations) that ends
with the usual output commands, in the usual order:

```
CHECK CODE ALL
PRINT JOINT COORDINATES
PRINT MEMBER INFORMATION
PRINT MEMBER PROPERTIES
UNIT INCH KIP
LOAD LIST <strength cases>
PRINT SUPPORT REACTIONS
PRINT MEMBER FORCES
LOAD LIST <service cases>
PRINT JOINT DISPLACEMENTS
PRINT SECTION DISPLACEMENTS
```

The reader (`scripts/lib/readers/AnlFrameReader.cs`) reads the member forces today and names the other tables as
"not read yet". Their parsers are to be written against the real layout of each table, which this probe's `.anl`
gives. The model is built to show what a table's layout depends on:

- two six-digit joint and member numbers (the column widths);
- enough rows for page breaks inside the force and displacement tables;
- one shape of each kind (W, channel, tee, double angle, single angle, tube, pipe, prismatic);
- a group name, a wrapped list and comment lines in the input (how STAAD echoes them);
- releases, an offset, BETA, truss members, three kinds of support;
- two load lists;
- a code check with members at TRACK 2, TRACK 1 and TRACK 0.

## Running it (production machine)

1. Open `anl-prints-probe.std` in STAAD.Pro and run the analysis.
   - If you normally use other design parameters (CODE, METHOD, TRACK ...), replace the block between the two
     rows of dashes near the end of the file with yours first. The code-check layout depends on them.
   - If the run stops on a command, note the message: that is a finding too.
2. Open the `.anl` in a text editor and **remove what names the licensee or the machine**, and nothing else:
   - the banner on the first page: the line naming who the program is licensed to;
   - any line with a folder path (the input file's path).

   Replace each with `<removed>` so the line count stays the same.
3. Save it beside the probe as `anl-prints-probe.anl`, commit it to this branch and push.
   - If STAAD saved the `.std` again in its own form (for example the section database form), commit that file
     too, as `anl-prints-probe-saved.std`.
