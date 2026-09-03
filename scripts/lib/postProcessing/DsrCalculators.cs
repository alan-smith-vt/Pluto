// STUB -- the production file was exported as a `// sanitized` husk (2026-09-01 audit).
// This file only restores the compile surface consumed by pipeline.cs and Types.cs so
// the single Add-Type batch in Config.ps1 succeeds. Re-hydrate the real bodies from the
// production environment (see vault/audits/scripts-sanitized-audit.md, re-hydration checklist).
// C# 5 only (Add-Type under PowerShell 5.1, warnings-as-errors).
using System;
using System.Collections.Generic;

// ACI design strengths for one element (the 17 STR-enum values incl. phi factors).
// Assigned to ElemProperties.Strengths (Types.cs) by the properties loader.
public class ElemStrength
{
	// sanitized: Pnc/Pnt/Pnb, Mn/Mnb (x,y), Vc, Vc_k, Vs, Vn + material constants
}

// Computes design strengths (strs) and demand/strength ratios (DSRs) per element corner.
public class DsrRunner
{
	public List<StrRecord> strs = new List<StrRecord>();

	public DsrRunner(GeometryResult geom, string propertiesPath, string propertiesPredicatePath)
	{
		throw new NotImplementedException(
			"DsrRunner is a sanitized stub: set writeStrs/writeDsrs = false, or re-hydrate DsrCalculators.cs.");
	}

	// Must emit exactly DSR.Count floats per record in enum order (validated by RawViewerWriter.AppendDsr).
	public List<DsrRecord> Calculate(List<StressRecord> stresses)
	{
		throw new NotImplementedException("DsrRunner.Calculate is a sanitized stub.");
	}
}
