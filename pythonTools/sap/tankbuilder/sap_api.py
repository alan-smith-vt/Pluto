"""SAP2000 OAPI driver (COM via comtypes): attach or start, open, run, results.

Only this module talks to SAP. Everything it returns is plain Python, and the
results go out as the same .s2k text tables SAP's own export writes, so
scripts/arms/SapToPluto.cs reads them unchanged.

    sap = SapSession.attach_or_start()
    sap.open(Path("models/example/example.s2k"))
    sap.run()
    Path("models/example/results.s2k").write_text(sap.results_s2k())

Facts checked against SAP2000 25.1.0 (2026-09-03):
  * CSI.SAP2000.API.SapObject needs early binding -> comtypes, not pywin32.
  * File.OpenFile accepts .s2k directly (imports it; ~20 s for 756 joints).
  * Results.JointDispl reports in the joint LOCAL axes (same as the table
    export), so SapToPluto's local->global rotation stays correct.
  * Results.* return full doubles; DatabaseTables.GetTableForDisplayArray is
    rounded to the display format -- do not use it for results.
"""

from __future__ import annotations

import time
from pathlib import Path

PROGID = "CSI.SAP2000.API.SapObject"

# Results.AreaForceShell return slots (after NumberResults):
#   Obj Elm PointElm LoadCase StepType StepNum F11 F22 F12 FMax FMin FAngle FVM
#   M11 M22 M12 MMax MMin MAngle V13 V23 VMax VAngle
_SHELL_FIELDS = ["Obj", "Elm", "PointElm", "LoadCase", "StepType", "StepNum",
                 "F11", "F22", "F12", "FMax", "FMin", "FAngle", "FVM",
                 "M11", "M22", "M12", "MMax", "MMin", "MAngle", "V13", "V23", "VMax", "VAngle"]
_DISP_FIELDS = ["Obj", "Elm", "LoadCase", "StepType", "StepNum", "U1", "U2", "U3", "R1", "R2", "R3"]


def _num(v) -> str:
    return repr(float(v))


class SapSession:
    """One attached SAP2000 instance."""

    def __init__(self, sap_object, started: bool):
        self.sap = sap_object
        self.model = sap_object.SapModel
        self.started = started   # True if we launched it (and may close it)

    # --- lifecycle ---------------------------------------------------------

    @classmethod
    def attach_or_start(cls, visible: bool = True) -> "SapSession":
        import comtypes.client as cc
        try:
            return cls(cc.GetActiveObject(PROGID), started=False)
        except OSError:
            pass
        helper = cc.CreateObject("SAP2000v1.Helper")
        helper = helper.QueryInterface(_helper_iface(helper))
        obj = helper.CreateObjectProgID(PROGID)
        ret = obj.ApplicationStart()
        if ret != 0:
            raise RuntimeError(f"SAP2000 ApplicationStart returned {ret}")
        if visible:
            obj.Visible()
        return cls(obj, started=True)

    def close(self, save: bool = False) -> None:
        if self.started:
            self.sap.ApplicationExit(save)

    @property
    def version(self) -> str:
        return self.model.GetVersion()[0]

    # --- model ----------------------------------------------------------------

    def open(self, s2k: Path, save_sdb: bool = True) -> Path:
        """Import a .s2k (or open an .sdb). Returns the .sdb SAP now holds."""
        t0 = time.time()
        ret = self.model.File.OpenFile(str(s2k))
        if ret != 0:
            raise RuntimeError(f"OpenFile({s2k}) returned {ret}")
        sdb = s2k.with_suffix(".sdb")
        if save_sdb and s2k.suffix.lower() != ".sdb":
            self.model.File.Save(str(sdb))
        self.open_seconds = time.time() - t0
        return sdb

    def counts(self) -> tuple[int, int, int]:
        m = self.model
        return m.PointObj.Count(), m.AreaObj.Count(), m.FrameObj.Count()

    def groups(self) -> list[str]:
        r = self.model.GroupDef.GetNameList()
        return list(r[1])

    def load_cases(self) -> list[str]:
        return list(self.model.LoadCases.GetNameList()[1])

    def run(self) -> float:
        t0 = time.time()
        ret = self.model.Analyze.RunAnalysis()
        if ret != 0:
            raise RuntimeError(f"RunAnalysis returned {ret}")
        return time.time() - t0

    # --- results ----------------------------------------------------------------

    def _select_all_cases(self) -> list[str]:
        setup = self.model.Results.Setup
        setup.DeselectAllCasesAndCombosForOutput()
        cases = self.load_cases()
        for c in cases:
            setup.SetCaseSelectedForOutput(c)
        return cases

    def joint_displacements(self) -> list[dict]:
        """One dict per joint per case, keys Joint / OutputCase / U1..R3 (local axes)."""
        self._select_all_cases()
        r = self.model.Results.JointDispl("ALL", 2)          # 2 = GroupElm
        n = r[0]
        cols = r[1:1 + len(_DISP_FIELDS)]
        out = []
        for i in range(n):
            row = {f: cols[k][i] for k, f in enumerate(_DISP_FIELDS)}
            out.append({"Joint": row["Obj"], "OutputCase": row["LoadCase"],
                        "CaseType": "LinStatic",
                        **{k: row[k] for k in ("U1", "U2", "U3", "R1", "R2", "R3")}})
        return out

    def shell_forces(self) -> list[dict]:
        """One dict per shell per joint per case, SAP 'Element Forces - Area Shells' columns."""
        self._select_all_cases()
        r = self.model.Results.AreaForceShell("ALL", 2)
        n = r[0]
        cols = r[1:1 + len(_SHELL_FIELDS)]
        out = []
        for i in range(n):
            row = {f: cols[k][i] for k, f in enumerate(_SHELL_FIELDS)}
            out.append({"Area": row["Obj"], "AreaElem": row["Elm"], "ShellType": "Shell-Thin",
                        "Joint": row["PointElm"], "OutputCase": row["LoadCase"], "CaseType": "LinStatic",
                        **{k: row[k] for k in ("F11", "F22", "F12", "FMax", "FMin", "FAngle", "FVM",
                                               "M11", "M22", "M12", "MMax", "MMin", "MAngle",
                                               "V13", "V23", "VMax", "VAngle")}})
        return out

    def results_s2k(self) -> str:
        """The two result tables as .s2k text (full precision), SapToPluto-ready."""
        lines = ['File generated by tankbuilder.sap_api (SAP2000 OAPI results)', "",
                 'TABLE:  "PROGRAM CONTROL"',
                 f'   ProgramName=SAP2000   Version={self.version}   CurrUnits="Kip, ft, F"', ""]
        for title, rows in (("JOINT DISPLACEMENTS", self.joint_displacements()),
                            ("ELEMENT FORCES - AREA SHELLS", self.shell_forces())):
            lines.append(f'TABLE:  "{title}"')
            for row in rows:
                parts = []
                for k, v in row.items():
                    parts.append(f"{k}={_num(v)}" if isinstance(v, float) else f"{k}={v}")
                lines.append("   " + "   ".join(parts))
            lines.append("")
        lines.append("END TABLE DATA")
        return "\n".join(lines) + "\n"


def _helper_iface(helper):
    """comtypes needs the cHelper interface to call CreateObjectProgID."""
    import comtypes.gen.SAP2000v1 as sap  # type: ignore  (generated on first CreateObject)
    return sap.cHelper
