// STUB -- the production file was exported as a `// sanitized` husk (2026-09-01 audit).
// Restores only the static call surface used by pipeline.cs (both callers sit behind
// default-false flags: config.rez, config.fMuCorrection, config.oldFmuCorrection).
// Re-hydrate from the production environment; keep C# 5 only.
using System;

// Voids stresses inside rigid end zones defined in the rezPath file.
public static class RigidEndZoneVoider
{
	public static void Apply(StaadModel model, string rezPath)
	{
		throw new NotImplementedException("RigidEndZoneVoider.Apply is a sanitized stub (set config.rez = false).");
	}
}

// PSDS f-mu correction against static settlement results (LC 35).
public static class PSDS_FmuCorrection
{
	public static void Apply(StaadModel model, string staticSettlementPath, bool expandCenter)
	{
		throw new NotImplementedException("PSDS_FmuCorrection.Apply is a sanitized stub (set config.fMuCorrection = false).");
	}

	// Deprecated debug variant.
	public static void ApplyOldCorrection(StaadModel model)
	{
		throw new NotImplementedException("PSDS_FmuCorrection.ApplyOldCorrection is a sanitized stub (set config.oldFmuCorrection = false).");
	}
}
