#!/usr/bin/env python3
r"""Whatever SAP2000 holds -> viewer .bin + .features.json, no config, no manual export.

    python export_model.py                          # the model open in SAP -> ~/Downloads/<name>.bin
    python export_model.py -o D:\x\viewer\Model     # output base (no extension)
    python export_model.py "D:\x\Model.sdb"         # open that file in the running SAP first (replaces what it holds)
    python export_model.py "D:\x\Model.sdb" --new-instance   # ... in a second SAP; the first is untouched
    python export_model.py --run                    # (re)run the analysis before pulling results
    python export_model.py --cylindrical            # tanks / silos: add Translation R / T
    python export_model.py --view                   # serve and open the viewer afterwards

What it does:
  attach   SapSession.attach_or_start, or a fresh SAP with --new-instance (closed at the
           end unless --keep-open); with a path, File.OpenFile it
  run      only with --run or when a case has no results. SAP saves the model when it runs,
           so the model is first saved AS <outBase>.sdb (a working copy) and every case is
           flagged to run; the file SAP opened is never written. Cases that still do not
           finish (e.g. staged construction without the licence) are reported and skipped.
  model    the ANALYSIS MESH, not the objects: PointElm / AreaElm / LineElm over the OAPI,
           renumbered 1..N (SapToPluto wants integer labels), with sections, local axes,
           restraints and SAP groups carried from the objects -> <outBase>.model.s2k, and
           the element / object names of every id -> <outBase>.labels.csv. Auto-meshed
           models thus come out at the mesh SAP analysed, which is where the results live;
           unmeshed models come out one-to-one with their objects.
  results  Results.JointDispl / AreaForceShell / AreaStressShell / FrameForce for every
           case, full precision; a case with several saved steps (staged construction)
           becomes one viewer case per step, <case>.<n> (--final-only: last step only)
           -> <outBase>.results.s2k (skipped with --no-results)
  export   scripts/arms/SapToPluto.cs via Windows PowerShell 5.1 -> <outBase>.bin + .features.json

Not carried: tendons, cables, links, solids (no viewer domain); frame end releases and
explicit insertion offsets; reactions and link forces (see vault/arms/sap-results-coverage).
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
REPO = HERE.parent.parent

from tankbuilder.sap_api import (PROGID, SapSession, _helper_iface, _DISP_FIELDS,  # noqa: E402
                                 _SHELL_FIELDS, _STRESS_FIELDS, _FRAME_FIELDS)

PS51 = r"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
GROUP_TYPES = {1: "point", 2: "frame", 5: "area"}      # GroupDef.GetAssignments object types we carry


def _q(v) -> str:
    v = str(v)
    return f'"{v}"' if (" " in v or "," in v or v == "") else v


def _row(**kv) -> str:
    return "   " + "   ".join(f"{k}={_q(v)}" for k, v in kv.items())


def new_session(exe: Path | None = None) -> SapSession:
    """A second SAP2000, whatever is already running (GetActiveObject only ever finds the first)."""
    import comtypes.client as cc
    helper = cc.CreateObject("SAP2000v1.Helper")
    helper = helper.QueryInterface(_helper_iface(helper))
    exe = exe or SapSession.find_exe()
    obj = helper.CreateObject(str(exe)) if exe else helper.CreateObjectProgID(PROGID)
    ret = obj.ApplicationStart()
    if ret != 0:
        raise RuntimeError(f"SAP2000 ApplicationStart returned {ret}")
    obj.Visible()
    return SapSession(obj, started=True)


def table_rows(model, name: str) -> list[dict]:
    """Rows of a DatabaseTables input table as dicts. An empty table comes back with
    None field names (SAP hands over stale buffers), hence the guard."""
    r = model.DatabaseTables.GetTableForDisplayArray(name, [], "")
    fields, nrec, data, ret = list(r[2]), int(r[3]), list(r[4]), int(r[5])
    if ret != 0 or nrec == 0 or not fields or any(f is None for f in fields):
        return []
    n = len(fields)
    return [{k: v for k, v in zip(fields, data[i:i + n]) if v is not None} for i in range(0, n * nrec, n)]


# --- analysis mesh -------------------------------------------------------------

class Mesh:
    def __init__(self, m):
        t0 = time.time()
        self.pid: dict[str, int] = {}
        self.coords: list[tuple[float, float, float]] = []
        for name in m.PointElm.GetNameList()[1]:
            x, y, z, _ = m.PointElm.GetCoordCartesian(name, 0.0, 0.0, 0.0, "Global")
            self.pid[name] = len(self.coords) + 1
            self.coords.append((x, y, z))
        self.point_axes = {}                                   # id -> (a, b, c) when not all zero
        for name, i in self.pid.items():
            a, b, c, _ = m.PointElm.GetLocalAxes(name, 0.0, 0.0, 0.0)
            if a or b or c:
                self.point_axes[i] = (a, b, c)

        self.aid: dict[str, int] = {}
        self.areas: list[tuple[str, list[int], str, str, float]] = []   # elm, point ids, section, obj, angle
        self.area_obj: dict[str, list[int]] = {}
        for name in m.AreaElm.GetNameList()[1]:
            n, pts, _ = m.AreaElm.GetPoints(name, 0, [])
            sect, _ = m.AreaElm.GetProperty(name, "")
            obj, _ = m.AreaElm.GetObj(name, "")
            ang, _ = m.AreaElm.GetLocalAxes(name, 0.0)
            i = len(self.areas) + 1
            self.aid[name] = i
            self.areas.append((name, [self.pid[p] for p in pts], sect, obj, ang))
            self.area_obj.setdefault(obj, []).append(i)

        self.lid: dict[str, int] = {}
        self.lines: list[tuple[str, int, int, str, str]] = []           # elm, i, j, section, obj
        self.line_obj: dict[str, list[int]] = {}
        self.skipped_lines: dict[int, int] = {}
        for name in m.LineElm.GetNameList()[1]:
            obj, otype, _rdi, _rdj, _ = m.LineElm.GetObj(name, "", 0)
            if otype != 0:                                     # 0 frame; cables / tendons have no beam domain
                self.skipped_lines[otype] = self.skipped_lines.get(otype, 0) + 1
                continue
            p1, p2, _ = m.LineElm.GetPoints(name, "", "")
            sect = m.LineElm.GetProperty(name, "", 0)[0]
            i = len(self.lines) + 1
            self.lid[name] = i
            self.lines.append((name, self.pid[p1], self.pid[p2], sect, obj))
            self.line_obj.setdefault(obj, []).append(i)
        self.seconds = time.time() - t0

    def point_of_obj(self, m, obj: str) -> int | None:
        name, ret = m.PointObj.GetElm(obj, "")
        return self.pid.get(name) if ret == 0 else None


def model_s2k(sap: SapSession, mesh: Mesh) -> str:
    m = sap.model
    L = ["File generated by export_model.py (SAP2000 OAPI, analysis mesh)", "",
         'TABLE:  "PROGRAM CONTROL"', _row(ProgramName="SAP2000", Version=sap.version, CurrUnits=sap.units), ""]

    def table(name, rows):
        if rows:
            L.append(f'TABLE:  "{name}"'); L.extend(rows); L.append("")

    table("COORDINATE SYSTEMS", [_row(**r) for r in table_rows(m, "Coordinate Systems")]
          or [_row(Name="GLOBAL", Type="Cartesian", X=0, Y=0, Z=0, AboutZ=0, AboutY=0, AboutX=0)])
    table("JOINT COORDINATES", [
        _row(Joint=i, CoordSys="GLOBAL", CoordType="Cartesian", XorR=repr(x), Y=repr(y), Z=repr(z),
             SpecialJt="No", GlobalX=repr(x), GlobalY=repr(y), GlobalZ=repr(z))
        for i, (x, y, z) in enumerate(mesh.coords, 1)])
    table("CONNECTIVITY - AREA", [
        _row(Area=i, NumJoints=len(p), **{f"Joint{k + 1}": pj for k, pj in enumerate(p)})
        for i, (_, p, _, _, _) in enumerate(mesh.areas, 1)])
    table("AREA SECTION ASSIGNMENTS", [_row(Area=i, Section=s, MatProp="Default")
                                       for i, (_, _, s, _, _) in enumerate(mesh.areas, 1)])
    table("AREA SECTION PROPERTIES", [_row(**r) for r in table_rows(m, "Area Section Properties")])
    table("AREA LOCAL AXES ASSIGNMENTS 1 - TYPICAL", [_row(Area=i, Angle=repr(a), AdvanceAxes="No")
                                                     for i, (_, _, _, _, a) in enumerate(mesh.areas, 1) if a])
    table("CONNECTIVITY - FRAME", [_row(Frame=i, JointI=a, JointJ=b, IsCurved="No")
                                   for i, (_, a, b, _, _) in enumerate(mesh.lines, 1)])
    table("FRAME SECTION ASSIGNMENTS", [_row(Frame=i, SectionType="N.A.", AutoSelect="N.A.", AnalSect=s,
                                             DesignSect="N.A.", MatProp="Default")
                                        for i, (_, _, _, s, _) in enumerate(mesh.lines, 1)])
    table("FRAME SECTION PROPERTIES 01 - GENERAL", [_row(**r) for r in table_rows(m, "Frame Section Properties 01 - General")])
    ins = []
    for r in table_rows(m, "Frame Insertion Point Assignments"):
        for i in mesh.line_obj.get(r.get("Frame", ""), []):
            ins.append(_row(**{**r, "Frame": i}))
    table("FRAME INSERTION POINT ASSIGNMENTS", ins)
    res = []
    for r in table_rows(m, "Joint Restraint Assignments"):
        i = mesh.point_of_obj(m, r.get("Joint", ""))
        if i:
            res.append(_row(**{**r, "Joint": i}))
    table("JOINT RESTRAINT ASSIGNMENTS", res)
    table("JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", [
        _row(Joint=i, AngleA=repr(a), AngleB=repr(b), AngleC=repr(c), AdvanceAxes="No")
        for i, (a, b, c) in sorted(mesh.point_axes.items())])
    grp = []
    for g in m.GroupDef.GetNameList()[1]:
        if g.upper() == "ALL":
            continue
        n, types, names = m.GroupDef.GetAssignments(g, 0, [], [])[:3]
        for t, o in zip(types, names):
            kind = GROUP_TYPES.get(int(t))
            if kind == "point":
                i = mesh.point_of_obj(m, o)
                if i:
                    grp.append(_row(GroupName=g, ObjectType="Joint", ObjectLabel=i))
            elif kind == "area":
                grp.extend(_row(GroupName=g, ObjectType="Area", ObjectLabel=i) for i in mesh.area_obj.get(o, []))
            elif kind == "frame":
                grp.extend(_row(GroupName=g, ObjectType="Frame", ObjectLabel=i) for i in mesh.line_obj.get(o, []))
    table("GROUPS 2 - ASSIGNMENTS", grp)
    L.append("END TABLE DATA")
    return "\n".join(L) + "\n"


def labels_csv(mesh: Mesh) -> str:
    L = ["kind,id,element,object"]
    for name, i in mesh.pid.items():
        L.append(f'joint,{i},"{name}",')
    L.extend(f'area,{i},"{e}","{o}"' for i, (e, _, _, o, _) in enumerate(mesh.areas, 1))
    L.extend(f'frame,{i},"{e}","{o}"' for i, (e, _, _, _, o) in enumerate(mesh.lines, 1))
    return "\n".join(L) + "\n"


# --- results -------------------------------------------------------------------

def write_results(sap: SapSession, mesh: Mesh, path: Path, final_only: bool = False) -> dict[str, int]:
    m = sap.model
    setup = m.Results.Setup
    setup.DeselectAllCasesAndCombosForOutput()
    setup.SetOptionNLStatic(2)                     # step-by-step: one row per saved step (option 1 gives Max/Min
    #                                                envelope rows for staged cases, principals zeroed)
    for c in sap.load_cases():
        setup.SetCaseSelectedForOutput(c)
    counts = {}
    with path.open("w") as f:
        f.write("File generated by export_model.py (SAP2000 OAPI results, analysis mesh)\n\n"
                'TABLE:  "PROGRAM CONTROL"\n' + _row(ProgramName="SAP2000", Version=sap.version, CurrUnits=sap.units) + "\n\n")

        def emit(title, fields, r, head):
            n = r[0]
            if not n:
                return
            cols = {k: r[1 + i] for i, k in enumerate(fields)}
            # a case with several saved steps (staged construction) becomes one viewer case per
            # step, "<case>.<step>" (or only its last step with final_only); single-step cases
            # keep their bare name
            steps: dict[str, set] = {}
            for c, st in zip(cols["LoadCase"], cols["StepNum"]):
                steps.setdefault(c, set()).add(float(st))
            multi = {c: max(st) for c, st in steps.items() if len(st) > 1}
            for c in sorted(multi):
                counts[f"  {c} steps"] = len(steps[c])
            f.write(f'TABLE:  "{title}"\n')
            written = 0
            for i in range(n):
                case = cols["LoadCase"][i]
                if case in multi:
                    step = float(cols["StepNum"][i])
                    if final_only:
                        if step != multi[case]:
                            continue
                    else:
                        case = f"{case}.{int(step)}"
                lead = head(cols, i, case)
                if lead is None:
                    continue
                f.write("   " + lead + "   " + "   ".join(f"{k}={float(cols[k][i])!r}" for k in fields if k in _NUM) + "\n")
                written += 1
            f.write("\n")
            counts[title] = written

        emit("JOINT DISPLACEMENTS", _DISP_FIELDS, m.Results.JointDispl("ALL", 2),
             lambda c, i, case: _lead(mesh.pid.get(c["Elm"][i]), case, "Joint"))
        emit("ELEMENT FORCES - AREA SHELLS", _SHELL_FIELDS, m.Results.AreaForceShell("ALL", 2),
             lambda c, i, case: _shell_lead(mesh, c, i, case))
        emit("ELEMENT STRESSES - AREA SHELLS", _STRESS_FIELDS, m.Results.AreaStressShell("ALL", 2),
             lambda c, i, case: _shell_lead(mesh, c, i, case))
        if mesh.lines:
            emit("ELEMENT FORCES - FRAMES", _FRAME_FIELDS, m.Results.FrameForce("ALL", 2),
                 lambda c, i, case: _frame_lead(mesh, c, i, case))
        f.write("END TABLE DATA\n")
    return counts


_NUM = {"U1", "U2", "U3", "R1", "R2", "R3",
        "F11", "F22", "F12", "FMax", "FMin", "FAngle", "FVM", "M11", "M22", "M12", "MMax", "MMin", "MAngle",
        "V13", "V23", "VMax", "VAngle",
        "S11Top", "S22Top", "S12Top", "SMaxTop", "SMinTop", "SAngleTop", "SVMTop",
        "S11Bot", "S22Bot", "S12Bot", "SMaxBot", "SMinBot", "SAngleBot", "SVMBot",
        "S13Avg", "S23Avg", "SMaxAvg", "SAngleAvg",
        "P", "V2", "V3", "T", "M2", "M3"}


def _lead(jid, case, key="Joint"):
    return None if jid is None else f"{key}={jid}   OutputCase={_q(case)}   CaseType=LinStatic"


def _shell_lead(mesh, c, i, case):
    a = mesh.aid.get(c["Elm"][i]); j = mesh.pid.get(c["PointElm"][i])
    if a is None or j is None:
        return None
    return f"Area={a}   AreaElem={a}   ShellType=Shell-Thin   Joint={j}   OutputCase={_q(case)}   CaseType=LinStatic"


def _frame_lead(mesh, c, i, case):
    fid = mesh.lid.get(c["Elm"][i])
    if fid is None:
        return None
    return f"Frame={fid}   Station={float(c['ElmSta'][i])!r}   OutputCase={_q(case)}   CaseType=LinStatic"


# --- run / export --------------------------------------------------------------

def case_status(sap: SapSession) -> list[tuple[str, int]]:
    st = sap.model.Analyze.GetCaseStatus()   # 1 not run, 2 could not start, 3 not finished, 4 finished
    return list(zip(st[1], (int(s) for s in st[2])))


def run_on_copy(sap: SapSession, copy: Path) -> None:
    m = sap.model
    if m.File.Save(str(copy)) != 0:
        raise SystemExit(f"could not save the working copy {copy}")
    m.Analyze.SetRunCaseFlag("", True, True)
    print(f"[run]     working copy {copy.name}; running every case ...")
    secs = sap.run()
    done = [c for c, s in case_status(sap) if s == 4]
    left = [c for c, s in case_status(sap) if s != 4]
    print(f"[run]     finished {', '.join(done)}  ({secs:.0f}s)")
    if left:
        print(f"[run]     NOT RUN: {', '.join(left)}")
        seen = set()
        for r in table_rows(m, "Analysis Messages"):
            msg = str(r.get("Message", "")).splitlines()
            if r.get("Type") == "Warning" and msg and msg[0] not in seen:
                seen.add(msg[0]); print("[run]       " + " ".join(msg[:2]))


def export(model: Path, results: Path | None, out_base: Path, model_id: str, cylindrical: bool) -> None:
    script = out_base.parent / f"{out_base.name}._export.ps1"
    res = f"'{results}'" if results else "$null"
    script.write_text(
        "$ErrorActionPreference = 'Stop'\n"
        f". '{REPO / 'scripts' / 'lib' / 'Config.ps1'}'\n"
        f"$r = [SapToPluto]::Export('{model}', {res}, '{out_base}', '{model_id}', {'$true' if cylindrical else '$false'})\n"
        "Write-Output $r.Summary()\n")
    r = subprocess.run([PS51, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                        "-File", str(script)], capture_output=True, text=True)
    for line in r.stdout.splitlines():
        if line.strip() and not line.startswith(("Nodes Extracted", "Elements Extracted")):
            print("[export]  " + line)
    if r.returncode != 0:
        print(r.stderr, file=sys.stderr)
        raise SystemExit(f"SapToPluto failed ({r.returncode})")
    script.unlink(missing_ok=True)


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("sdb", nargs="?", type=Path, help="model to open first (default: the one SAP holds)")
    p.add_argument("-o", "--out", type=Path, help="output base path, no extension (default: ~/Downloads/<model name>)")
    p.add_argument("--new-instance", action="store_true", help="start a second SAP2000 instead of attaching")
    p.add_argument("--keep-open", action="store_true", help="with --new-instance: leave that SAP running afterwards")
    p.add_argument("--run", action="store_true", help="run the analysis first (default: only if results are missing)")
    p.add_argument("--no-results", action="store_true", help="geometry only")
    p.add_argument("--final-only", action="store_true",
                   help="staged / multi-step cases: only the last step, under the bare case name (default: every step as <case>.<n>)")
    p.add_argument("--cylindrical", action="store_true", help="add Translation R / T about the plan centroid")
    p.add_argument("--view", action="store_true", help="serve the repo and open the viewer on the result")
    p.add_argument("--port", type=int, default=8765)
    a = p.parse_args(argv)

    sap = new_session() if a.new_instance else SapSession.attach_or_start()
    print(f"[sap]     SAP2000 {sap.version} ({'new instance' if sap.started else 'attached to the running instance'})")
    try:
        if a.sdb:
            sap.open(a.sdb.resolve(), save_sdb=False)
        held = sap.model.GetModelFilename(True)
        if not held:
            raise SystemExit("SAP holds no model. Pass a .sdb path.")
        held = Path(held)
        j, ar, f = sap.counts()
        print(f"[open]    {held}  ({j} joints, {ar} areas, {f} frames, units {sap.units})")
        stem = held.stem.replace(" ", "_")
        out_base = (a.out or Path.home() / "Downloads" / stem).resolve()
        out_base.parent.mkdir(parents=True, exist_ok=True)

        if not a.no_results and (a.run or any(s != 4 for _, s in case_status(sap))):
            run_on_copy(sap, out_base.with_suffix(".sdb"))

        mesh = Mesh(sap.model)
        skipped = ", ".join(f"type {t}: {n}" for t, n in mesh.skipped_lines.items())
        print(f"[mesh]    {len(mesh.coords)} points, {len(mesh.areas)} shells, {len(mesh.lines)} frame elements"
              f"{'; line elements skipped ' + skipped if skipped else ''}  ({mesh.seconds:.0f}s)")
        model_path = out_base.with_name(out_base.name + ".model.s2k")
        model_path.write_text(model_s2k(sap, mesh))
        out_base.with_name(out_base.name + ".labels.csv").write_text(labels_csv(mesh))

        results_path = None
        if not a.no_results:
            results_path = out_base.with_name(out_base.name + ".results.s2k")
            counts = write_results(sap, mesh, results_path, a.final_only)
            print(f"[results] " + ", ".join(f"{k.split(' - ')[0].title()} {v}" for k, v in counts.items())
                  + f"  ({results_path.stat().st_size >> 20} MB)")

        export(model_path, results_path, out_base, f"sap/{stem}", a.cylindrical)
        print(f"[done]    {out_base}.bin  +  {out_base.name}.features.json")
    finally:
        if sap.started and not a.keep_open:
            sap.close(save=False)

    if a.view:
        from run_sap import step_viewer
        step_viewer(out_base.with_suffix(".bin"), out_base.with_name(out_base.name + ".features.json"), a.port)
    return 0


if __name__ == "__main__":
    sys.exit(main())
