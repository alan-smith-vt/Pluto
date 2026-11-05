using System.Collections.Generic;

// FramePreview  --  what a caller of SapToPluto knows beyond the .s2k text (2026-11-05): where the model came from
// (model.source) and the build's own messages for the viewer's Checks panel (the review section). The frame workbook's
// preview fills both; a plain SAP export takes the source from its file. Node and element numbers in a message are the
// viewer's ids (SapLabels). C# 5 (prefix Frame).

public sealed class FramePreview
{
    public string SourceJson;                 // model.source as a raw JSON object (PipeSidecar.SourceOf); null = none
    public readonly List<PipeReviewItem> Messages = new List<PipeReviewItem>();

    public PipeReviewItem AddMessage(string level, string code, string text)
    {
        var m = new PipeReviewItem(level, code, text);
        Messages.Add(m);
        return m;
    }
}
