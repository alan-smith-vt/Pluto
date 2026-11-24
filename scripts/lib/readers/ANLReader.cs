using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Web.Script.Serialization;

// Index type-esque class
public class AnlSection
{
	//get; set; to mark as "properties" to allow serialization

	public string Name { get; set; }            // e.g. "PRINT ELEMENT STRESSES"
	public long StartOffset { get; set; }       // byte offset to section header line
	public long EndOffset { get; set; }         // byte offset to next section (or EOF)
	public LengthUnit Length { get; set; }
	public ForceUnit Force { get; set; }
	
	public AnlSection(string name, long start)
	{
		Name = name;
		StartOffset = start;
		EndOffset = -1;
	}

	public AnlSection() { } //Default constructor for json serialization
}

public class AnlIndexer
{
	//Note even blocks we're not using are necessary to mark the end of a block we are using
	private static readonly string[] SectionMarkers = new string[]
	{
		"UNIT",
		"PRINT ELEMENT STRESSES",
		"PRINT MEMBER SECTION FORCES",
		"PRINT ELEMENT JOINT STRESSES",
		"PRINT JOINT DISPLACEMENTS",
		"JOINT COORDINATES",
		"MEMBER INCIDENCES",
		"ELEMENT INCIDENCES SHELL",
		"START GROUP DEFINITION",
		"ELEMENT PROPERTY",
		"LOAD",
		"PRINT MEMBER FORCES",
		"PRINT MEMBER",
		"MEMBER LOAD",
		"MEMBER PROPERTY",
		"ELEM THICKNESS",
		"DEFINE MATERIAL START",
		"CONSTANTS",
		"SUPPORTS",
		"MEMBER OFFSET",
		"MEMBER RELEASE",
		"ELEMENT OFFSET",
		"ELEMENT RELEASE",
		"LOAD",
		"ELEMENT LOAD",
		"JOINT LOAD",
		"PERFORM ANALYSIS",
		"FINISH"
	};

	// Commands that only end the section before them (2026-11-06, frame models). They match only on STAAD's command
	// echo ("  NN. TEXT"), as a whole word, so a section now ends where the next command starts instead of at the next
	// marker above: a PRINT MEMBER FORCES section no longer runs on through a support reaction print or the CHECK CODE
	// output, whose numeric rows read as member forces. EchoFirstMarkers are tried before SectionMarkers ("LOAD" would
	// take them), EchoOnlyMarkers after (the specific PRINT ... markers above win over the generic PRINT).
	private static readonly string[] EchoFirstMarkers = new string[]
	{
		"LOAD LIST",
		"REPEAT LOAD"
	};

	private static readonly string[] EchoOnlyMarkers = new string[]
	{
		"PRINT SUPPORT REACTION",
		"PRINT",
		"PARAMETER",
		"PARAMETERS",
		"CHECK CODE",
		"SELECT",
		"SET",
		"DEFINE",
		"PDELTA",
		"PERFORM",
		"STEEL",
		"CODE",
		"MEMBER TRUSS",
		"MEMBER TENSION",
		"MEMBER COMPRESSION",
		"MEMBER CABLE",
		"SPRING",
		"INACTIVE",
		"CHANGE",
		"CUT OFF",
		"START",
		"END"
	};

	// The input commands a .std needs marked as well (StaadInput, 2026-11-23): each ends the section before it. An .anl is
	// indexed as it always was, without them. Never "STAAD SPACE": every .anl page header starts with it.
	public static readonly string[] StdMarkers = new string[]
	{
		"MEMBER TRUSS",
		"ELEMENT PLANE STRESS",
		"END GROUP DEFINITION",
		"END DEFINE MATERIAL",
		"DEFINE REFERENCE LOADS",
		"END DEFINE REFERENCE LOADS",
		"REFERENCE LOAD",
		"SELFWEIGHT",
		"TEMPERATURE LOAD",
		"SUPPORT DISPLACEMENT LOAD",
		"PDELTA"
	};

	private static readonly HashSet<byte> InterestingFirstBytes = BuildInterestingBytes();

	private static HashSet<byte> BuildInterestingBytes()
	{
		var set = new HashSet<byte>();
		foreach (string marker in SectionMarkers)
		{
			set.Add((byte)marker[0]);
		}
		foreach (string marker in EchoFirstMarkers)
		{
			set.Add((byte)marker[0]);
		}
		foreach (string marker in EchoOnlyMarkers)
		{
			set.Add((byte)marker[0]);
		}
		return set;
	}

	// // HashSet to support other matches later like "J" for joints
	// private static readonly HashSet<byte> InterestingFirstBytes = new HashSet<byte>
	// {
	// (byte)'P', (byte)'J', (byte)'M', (byte)'E', (byte)'S', (byte)'D', (byte)'L'
	// };

	// 	Streams the entire file, recording byte offsets of each section header
	public static List<AnlSection> IndexFile(string path)
	{
		return IndexFile(path, true, null);
	}

	// progress false draws no progress bar (a small file); extraMarkers (StdMarkers) are matched before the .anl's own
	public static List<AnlSection> IndexFile(string path, bool progress, string[] extraMarkers)
	{
		string[] markers = SectionMarkers;
		HashSet<byte> interesting = InterestingFirstBytes;
		bool echoMarkers = true;	// an .anl: the echo-only markers end sections too; a .std has no command echo
		if (extraMarkers != null && extraMarkers.Length > 0)
		{
			markers = extraMarkers.Concat(SectionMarkers).ToArray();
			interesting = new HashSet<byte>(markers.Select(m => (byte)m[0]));
			echoMarkers = false;
		}
		var sections = new List<AnlSection>();
		AnlSection current = null;
		long fileLength = new FileInfo(path).Length;
		const long progressInterval = 10L * 1024 * 1024; // 10MB
		int totalTicks = (int)(fileLength / progressInterval) + 1;
		long nextProgressAt = 0;
		ProgressBar pb = progress ? new ProgressBar(totalTicks, title: "ANL First Pass") : null;

		// 	Track position manually because StreamReader is not reliable for this
		//	use case
		using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
							FileShare.Read, bufferSize: 1 << 20)) //1 MB buffer
		{
			long lineStartOffset = 0;

			var lineReader = new ByteTrackingLineReader(fs);//our custom class
			byte[] lineBuf = new byte[4096];
			int lineLen;

			while ((lineLen = lineReader.ReadLineBytes(lineBuf, out lineStartOffset)) >= 0)
			{
				// Progress update every ~10MB with progressBar
				if (lineStartOffset >= nextProgressAt)
				{
					if (pb != null) pb.Tick();
					nextProgressAt = lineStartOffset + progressInterval;
				}

				if (lineLen == 0)
					continue;

				// Skip leading whitespace in the raw bytes
				int start = 0;
				while (start < lineLen && (lineBuf[start] == (byte)' ' || lineBuf[start] == (byte)'\t'))
					start++;

				if (start >= lineLen)
					continue;

				byte firstByte = lineBuf[start];

				int contentStart = start;
				bool hasEcho = false;	// the line carries STAAD's "NN." command echo prefix
				// Section headers start with a non-zero number
				if (firstByte > (byte)'0' && firstByte <= (byte)'9')
				{
					// Scan for "1234." command number prefix
					int dotIdx = -1;
					for (int i = start; i < lineLen && i < start + 15; i++)
					{
						if (lineBuf[i] == (byte)'.')
						{
							dotIdx = i;
							break;
						}
						// We can have zeros in the command number prefix
						if (lineBuf[i] < (byte)'0' || lineBuf[i] > (byte)'9')
							break;
					}

					if (dotIdx > start)
					{
						hasEcho = true;
						contentStart = dotIdx + 1;
						while (contentStart < lineLen &&
							(lineBuf[contentStart] == (byte)' ' || lineBuf[contentStart] == (byte)'\t'))
						{
							contentStart++;
						}
					}
					else
					{
						// Pure numeric line with no dot - data row, skip
						continue;
					}
				}

				if (contentStart >= lineLen)
					continue;

				byte contentByte = lineBuf[contentStart];
				if (!interesting.Contains(contentByte))
					continue;

				// Strings are expensive
				string trimmed = Encoding.UTF8.GetString(lineBuf, contentStart, lineLen - contentStart);

				// Check if this line is a known section
				string matchedSection = MatchSectionHeader(trimmed, markers, hasEcho && echoMarkers);

				//if (lineStartOffset > 822742L && lineStartOffset < 1040117L)
				// Console.WriteLine("Testing");
				// Console.Error.WriteLine("@{0}: [{1}]", lineStartOffset, matchedSection);

				if (matchedSection != null)
				{
					// Close previous section
					if (current != null)
						current.EndOffset = lineStartOffset;
					current = new AnlSection(matchedSection, lineStartOffset);
					sections.Add(current);
					continue;
				}

				// TODO add checkpoint / sub chunk logic here if needed

			}

			// Close final sections
			if (current != null)
				current.EndOffset = fs.Length;
		}
		if (pb != null) pb.Finish();
		return sections;
	}

	// markers: the section markers of this file kind (an .anl's, or a .std's input commands before them). hasEcho: the line
	// carries STAAD's "NN." command echo, so the echo-only markers apply too (an .anl; a .std has no echo).
	private static string MatchSectionHeader(string trimmedLine, string[] markers, bool hasEcho)
	{
		string upper = trimmedLine.ToUpperInvariant();
		if (hasEcho)
		{
			for (int i = 0; i < EchoFirstMarkers.Length; i++)
			{
				if (StartsWithWord(upper, EchoFirstMarkers[i]))
					return EchoFirstMarkers[i];
			}
		}
		for (int i = 0; i < markers.Length; i++)
		{
			if (upper.StartsWith(markers[i]))
				return markers[i];
		}
		if (hasEcho)
		{
			for (int i = 0; i < EchoOnlyMarkers.Length; i++)
			{
				if (StartsWithWord(upper, EchoOnlyMarkers[i]))
					return EchoOnlyMarkers[i];
			}
		}
		return null;
	}

	// "PRINT" matches "PRINT ANALYSIS RESULTS" but "SET" does not match "SETTLE"
	private static bool StartsWithWord(string upper, string marker)
	{
		if (!upper.StartsWith(marker)) return false;
		if (upper.Length == marker.Length) return true;
		char c = upper[marker.Length];
		return c == ' ' || c == '\t' || c == '\r';
	}
}

public static class AnlUnitStamper
{
	public static void StampUnits(string anlPath, List<AnlSection> sections)
	{
		LengthUnit curLength = LengthUnit.Inch; // Staad default
		ForceUnit curForce = ForceUnit.Kip; // Staad default
		
		foreach (AnlSection section in sections)
		{
			if (section.Name.ToUpperInvariant() == "UNIT")
			{
				string firstLine = ReadFirstLine(anlPath, section);
				ParseUnitLine(firstLine, ref curLength, ref curForce);
			}
			
			section.Length = curLength;
			section.Force = curForce;
		}		
	}
	
	// Helper Functions
	private static string ReadFirstLine(string anlPath, AnlSection section)
	{
		long length = section.EndOffset - section.StartOffset;
		byte[] buf = new byte[length];
		using (var fs = new FileStream(anlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			fs.Seek(section.StartOffset, SeekOrigin.Begin);
			fs.Read(buf, 0, (int)length);
		}
		
		string text = Encoding.UTF8.GetString(buf);
		
		string[] cleanSplit = text.Split('\n');

		return cleanSplit[0];
	}

	private static void ParseUnitLine(string line, ref LengthUnit length, ref ForceUnit force)
	{
		// Console.WriteLine("Parsing Units line: " + line);
		string[] tokens = line.Split(new char[] { ' ', '\t', '\r', '\n' },
			StringSplitOptions.RemoveEmptyEntries);
		
		for (int i = 0; i < tokens.Length; i++)
		{
			string tok = tokens[i].ToUpperInvariant();
			// Console.WriteLine(string.Format("Checking token: {0}, length: {1}",tok,tok.Length));
			if (tok == "UNIT") continue;
			
			LengthUnit l;
			if (TryLength(tok, out l)) 
			{ 
				length = l; 
				// Console.WriteLine("Changed units to " + l);
				continue; 
			}
			
			ForceUnit f;
			if (TryForce(tok, out f)) 
			{
				force = f;
				// Console.WriteLine("Changed units to " + f);
				continue;
			}
			
			// unrecognized token, do nothing
		}
	}
	
	// TODO verify abbreviations for metric units
	private static bool TryLength(string tok, out LengthUnit l)
	{
		l = LengthUnit.Inch;
		switch (tok)
		{
			case "INCH": case "INCHES": case "IN": l = LengthUnit.Inch; return true;
			case "FEET": case "FOOT": case "FT": l = LengthUnit.Foot; return true;
			case "METER": case "METRE": case "M": l = LengthUnit.Meter; return true;
			case "MMS": case "MM": l = LengthUnit.Millimeter; return true;
			case "CM": l = LengthUnit.Centimeter; return true;
			default: return false;
		}
	}
	
	private static bool TryForce(string tok, out ForceUnit f)
	{
		f = ForceUnit.Kip;
		switch (tok)
		{
			case "POUND": case "LB": case "LBS": f = ForceUnit.Pound; return true;
			case "KIP": case "KIPS": f = ForceUnit.Kip; return true;
			case "KN": f = ForceUnit.KiloNewton; return true;
			case "NEWTON": case "N": f = ForceUnit.Newton; return true;
			case "MTON": f = ForceUnit.MetricTon; return true;
			default: return false;
		}
	}
}

// Manual line reader from FileStream while tracking exact byte offsets.
public class ByteTrackingLineReader
{
	private readonly FileStream _fs;
	private readonly byte[] _buf;
	private int _bufLen;
	private int _bufPos;
	private long _bufFileOffset; // file offset where _buf[0] was read from
	private readonly StringBuilder _sb;
	private bool _eof;

	public long Position { get { return _bufFileOffset + _bufPos; } }

	public ByteTrackingLineReader(FileStream fs, int bufferSize = 1 << 20)
	{
		_fs = fs;
		_buf = new byte[bufferSize];
		_bufLen = 0;
		_bufPos = 0;
		_bufFileOffset = 0;
		_sb = new StringBuilder(512);
		_eof = false;
	}

	// Reads one line of bytes. Returns count of bytes in the line
	// or -1 at EOF. Writes the bytes to the buffer as it goes
	public int ReadLineBytes(byte[] lineBuf, out long lineStartOffset)
	{
		lineStartOffset = _bufFileOffset + _bufPos;

		if (_eof && _bufPos >= _bufLen)
			return -1;

		int writePos = 0;
		bool foundLine = false;

		while (!foundLine)
		{
			if (_bufPos >= _bufLen)
			{
				_bufFileOffset = _fs.Position;
				_bufLen = _fs.Read(_buf, 0, _buf.Length);
				_bufPos = 0;

				if (_bufLen == 0)
				{
					_eof = true;
					return writePos > 0 ? writePos : -1;
				}
			}

			while (_bufPos < _bufLen)
			{
				byte b = _buf[_bufPos];
				if (b == (byte)'\n' || b == (byte)';')
				{
					_bufPos++;
					// Strip trailing \r
					if (writePos > 0 && lineBuf[writePos - 1] == (byte)'\r')
						writePos--;
					foundLine = true;
					break;
				}

				// Guard against extremely long lines
				if (writePos < lineBuf.Length)
					lineBuf[writePos++] = b;

				_bufPos++;
			}
		}

		return writePos;
	}
}

// ------ Navigation helpers -------------
public abstract class AnlBlockParser
{
	protected readonly string _path;
	protected readonly byte[] _lineBuf1 = new byte[4096];
	protected readonly byte[] _lineBuf2 = new byte[4096];
	protected readonly float[] _floatBuf = new float[20];
	protected ByteTrackingLineReader _reader;
	protected FileStream _fs;

	// Progress
	protected long _nextProgressAt;
	protected long _progressInterval;
	protected ProgressBar _pb;
	public bool Quiet;                      // no progress bar and no console lines (a .std: StaadInput)

	protected AnlBlockParser(string path) { _path = path; }

	// MVP of the script, skip past the useless lines as efficiently as possible
	// Combination of skip AND reading/returning the line that we skip to
	public int SkipToNextDataLine(long endOffset, byte[] targetBuf, bool acceptJ)
	{
		while (true)
		{
			long offset;
			int len = _reader.ReadLineBytes(targetBuf, out offset);
			if (len < 0 || offset >= endOffset) return -1;

			// Find first non-whitespace byte
			int start = 0;
			while (start < len && (targetBuf[start] == (byte)' ' || targetBuf[start] == (byte)'\t'))
				start++;
			if (start >= len) continue;

			byte b = targetBuf[start];
			if (b == (byte)'*') continue; // skip lines that start with comment

			bool isData = ((b >= (byte)'0' && b <= (byte)'9') || (b == (byte)'-'));
			bool isJoint = acceptJ && (b == (byte)'J');

			if (isData || isJoint)
			{
				return len;
			}
		}
	}

	// Stand-alone read single line helper
	public int ReadNextLine(long endOffset, byte[] targetBuf)
	{
		long offset;
		int len = _reader.ReadLineBytes(targetBuf, out offset);
		if (len < 0 || offset >= endOffset) return -1;
		return len;
	}

	// Explicit line skip
	public void SkipLines(int count, long endOffset)
	{
		// Use _lineBuf1 as scratch - contents don't matter
		for (int i = 0; i < count; i++)
		{
			long offset;
			int len = _reader.ReadLineBytes(_lineBuf1, out offset);
			if (len < 0 || offset >= endOffset) return;
		}
	}

	// -----Float extraction from raw bytes ---------

	// Handles:
	//		- Whitespace-separated values 		"12.345 98.765"
	//		- Packed negatives (silly staad)	"12345-98.765"
	//		- Exponents (not sure if needed)	"1.23E+04"

	public static int ExtractFloats(byte[] buf, int start, int len, float[] output)
	{
		int count = 0;
		int i = start;
		int end = start + len;
		int numStart, numLen;

		while (count < output.Length && NextNumber(buf, ref i, end, out numStart, out numLen))
		{
			// TODO update this to do custom byte based string -> float parsing if slow
			string numStr = Encoding.UTF8.GetString(buf, numStart, numLen);
			float val;
			if (float.TryParse(numStr, NumberStyles.Float,
							CultureInfo.InvariantCulture, out val))
			{
				output[count++] = val; // Populate the output list (ref) with floats
			}
		}

		return count; // return count of numbers found
	}

	// The same numbers as doubles, as written (a .std's coordinates: StaadInput)
	public static int ExtractDoubles(byte[] buf, int start, int len, double[] output)
	{
		int count = 0;
		int i = start;
		int end = start + len;
		int numStart, numLen;

		while (count < output.Length && NextNumber(buf, ref i, end, out numStart, out numLen))
		{
			double val;
			if (double.TryParse(Encoding.UTF8.GetString(buf, numStart, numLen), NumberStyles.Float,
							CultureInfo.InvariantCulture, out val))
				output[count++] = val;
		}

		return count;
	}

	// The next number token from i (both of the above): false at the end of the line
	private static bool NextNumber(byte[] buf, ref int i, int end, out int numStart, out int numLen)
	{
		numStart = 0;
		numLen = 0;
		while (i < end)
		{
			// Skip whitespace
			while (i < end && (buf[i] == (byte)' ' || buf[i] == (byte)'\t'))
				i++;

			if (i >= end) break;

			// Check if this could start a number (not sure if + is needed)
			byte peek = buf[i];
			bool couldBeNumber = (peek >= (byte)'0' && peek <= (byte)'9')
								|| peek == (byte)'-'
								|| peek == (byte)'+'
								|| peek == (byte)'.';

			if (!couldBeNumber)
			{
				// Skip non-numeric token (e.g. "JOINT", column headers)
				while (i < end && buf[i] != (byte)' ' && buf[i] != (byte)'\t')
					i++;
				continue;
			}

			numStart = i;
			bool seenDigit = false;
			bool seenE = false;

			// Consume the leading sign, again not sure if + is needed
			if (buf[i] == (byte)'-' || buf[i] == (byte)'+')
				i++;

			while (i < end)
			{
				byte b = buf[i];

				if (b >= (byte)'0' && b <= (byte)'9')
				{
					seenDigit = true;
					seenE = false;
					i++;
				}
				else if (b == (byte)'.')
				{
					seenE = false;
					i++;
				}
				else if (b == (byte)'E' || b == (byte)'e')
				{
					seenE = true;
					i++;
					if (i < end && (buf[i] == (byte)'-' || buf[i] == (byte)'+'))
						i++;
				}
				else if (b == (byte)'-')
				{
					// Packed negative: "12.345-9.876"
					if (seenDigit && !seenE)
						break; // '-' starts next number
					else
						i++;
				}
				else if (b == (byte)' ' || b == (byte)'\t')
				{
					break; // space or tab delimited
				}
				else
				{
					// Unknown character - skip the rest of token and print to console because we messed up
					while (i < end && buf[i] != (byte)' ' && buf[i] != (byte)'\t')
						i++;
					//TODO print buf from numStart to numStart + numLen as string
					break;
				}
			}

			numLen = i - numStart;
			if (numLen > 0 && seenDigit)
				return true;
		}

		numStart = 0;
		numLen = 0;
		return false;
	}

	public void CheckProgress()
	{
		if (_pb != null && _reader.Position >= _nextProgressAt)
		{
			_pb.Tick();
			_nextProgressAt = _reader.Position + _progressInterval;
		}
	}

}

// Parse the stress chunk defined by two offsets
//	Supports joint and center stresses 
// Units converted for stresses, forces (TODO), and nodes
public class AnlStressParser : AnlBlockParser
{
	// Results
	public List<StressRecord> Stresses = new List<StressRecord>();

	// Working memory for the joint stress blocks: center stress entry provides
	// element ID and LC that apply to subsequent joint stress entries
	private int _currentElementId;
	private int _currentLoadCase;
	private Dictionary<int, Element> _elems;
	private CoordFrame _targetState;
	private bool _progress;
	private AnlSection _section;

	public AnlStressParser(string path) : base(path) { }

	public void ParseSection(AnlSection section, Dictionary<int, Element> elems, CoordFrame targetState, bool progress = true)
	{
		_elems = elems;
		_targetState = targetState;
		_progress = progress;
		_section = section;

		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);

		if (progress) { _pb = new ProgressBar(totalTicks, title: title); }

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;
			bool isJointBlock = section.Name.Contains("JOINT");

			// Skip the PRINT command since it starts with a number
			SkipLines(1, endOffset);

			// Two modes, center and joint. Branch here based on supplied section name
			if (isJointBlock)
			{
				ParseJointStressBlock(endOffset);
			}
			else
			{
				ParseElementStressBlock(endOffset);
			}

			if (_progress) { _pb.Finish(); }
		}
	}

	// Entry for element stresses
	private void ParseElementStressBlock(long endOffset)
	{
		// Skip and read functions respect the endOffset of this chunk of data
		//	and return null when we reach the end of the data block
		while (true)
		{
			if (_progress) { CheckProgress(); }
			// 1. Skip to next line starting with a number or '-'
			int len1 = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
			if (len1 == -1) break;

			// 1.5. Read the next line too
			int len2 = ReadNextLine(endOffset, _lineBuf2);
			if (len2 == -1) break;

			// 2. Parse these two lines as center stresses
			ParseStressPair(_lineBuf1, len1,
							_lineBuf2, len2,
							isJoint: false);

			// 3. Skip 1 line (Trescat)
			SkipLines(1, endOffset);
		}
	}

	// Entry for joint stresses
	private void ParseJointStressBlock(long endOffset)
	{
		_currentElementId = -1;
		_currentLoadCase = -1;

		// Skip and read functions respect the endOffset of this chunk of data
		//	and return -1 when we reach the end of the data block
		while (true)
		{
			if (_progress) { CheckProgress(); }
			// 1. Skip to next line starting with a number or '-'
			int len1 = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: true);
			if (len1 == -1) break;

			// 1.5. Read the next line too
			int len2 = ReadNextLine(endOffset, _lineBuf2);
			if (len2 == -1) break;

			// Determine type by first non-whitespace byte
			//	i.e. joint or center stress entry (since joint blocks have both)
			int start = 0;
			while (start < len1 &&
					(_lineBuf1[start] == (byte)' ' || _lineBuf1[start] == (byte)'\t'))
				start++;

			byte firstByte = _lineBuf1[start];
			bool isJointLine = (firstByte == (byte)'J');

			// 2. Parse this line + next line as center stresses or joint stresses based on the above
			ParseStressPair(_lineBuf1, len1,
							_lineBuf2, len2,
							isJoint: isJointLine);

			// 3. Skip 1 line (Trescat)
			SkipLines(1, endOffset);
		}
	}

	// Stress record extraction
	private void ParseStressPair(byte[] buf1, int len1, byte[] buf2, int len2, bool isJoint)
	{
		// Center stress pattern
		// Row 1: ElemId LC	SQX		SQY 	MX 	MY 	MXY 	(2 ints 5 floats)
		// Row 2: 			VONT	VONTB	SX	SY	SXY		(5 floats, skip first 2)
		// Row 3:			TRESCAT	TRESCAB					(skip line)

		// Joint stress pattern
		// Row 1: JOINT		SQX		SQY		MX	MY	MXY		(1 string, 5 floats)
		// Row 2:  nid		VONT	VONTB	SX	SY	SXY		(1 int, 5 floats, skip first 2 floats)
		// Row 3: TOP : SMAX ...							(skip line)

		int n1 = ExtractFloats(buf1, 0, len1, _floatBuf);
		string debug = Encoding.UTF8.GetString(buf1, 0, len1);
		// Console.Error.WriteLine(debug);
		// Console.Error.WriteLine(string.Format("n1: {0}",n1));
		// Console.Error.WriteLine(string.Join(", ", _floatBuf));

		var rec = new StressRecord();

		// To get to a joint row we must have necessarily past a center stress row
		//	so element id and load case should be accurate in memory
		if (isJoint)
		{
			// Row 1: "JOINT" skipped by ExtractFloats -> SQX SQY MX MY MXY
			if (n1 < 5) return;

			rec.elemID = _currentElementId;
			rec.LC = _currentLoadCase;

			rec.Sf = new float[8];

			//				0	1	2	3	4	5	6	7
			//rec.Sf = 	[	Sx 	Sxy SQx Sy 	SQy	Mx	Mxy	My ]

			//				0	1	2	3	4
			//_floatBuf = [	SQx	SQy	Mx	My	Mxy ]

			rec.Sf[2] = _floatBuf[0]; // SQx
			rec.Sf[4] = _floatBuf[1]; // SQy
			rec.Sf[5] = _floatBuf[2]; // Mx
			rec.Sf[7] = _floatBuf[3]; // My
			rec.Sf[6] = _floatBuf[4]; // Mxy

			int n2 = ExtractFloats(buf2, 0, len2, _floatBuf);
			if (n2 < 6) return;

			//				0	1	2	3	4	5
			//_floatBuf = [	nid	~	~	Sx	Sy	Sxy]

			rec.node = (int)_floatBuf[0]; //node id
			rec.Sf[0] = _floatBuf[3]; // Sx
			rec.Sf[3] = _floatBuf[4]; // Sy
			rec.Sf[1] = _floatBuf[5]; // Sxy
		}

		// Center stress entry
		else
		{
			// For first element: 	ElemId 	LC SQX SQY MX MY MXY (7 floats)
			// Subsequent elements: 		LC SQX SQY MX MY MXY (6 floats)
			if (n1 == 7)
			{
				//					0	1	2	3	4	5	6
				//_floatBuf = [	ElemId 	LC	SQx	SQy	Mx	My	Mxy ]

				//				0	1	2	3	4	5	6	7
				//rec.Sf = 	[	Sx 	Sxy SQx Sy 	SQy	Mx	Mxy	My ]
				rec.elemID = (int)_floatBuf[0];
				rec.LC = (int)_floatBuf[1];
				rec.node = -1; //node id -1 for center
				rec.Sf = new float[8];

				rec.Sf[2] = _floatBuf[2]; // SQx
				rec.Sf[4] = _floatBuf[3]; // SQy
				rec.Sf[5] = _floatBuf[4]; // Mx
				rec.Sf[7] = _floatBuf[5]; // My
				rec.Sf[6] = _floatBuf[6]; // Mxy

				_currentElementId = rec.elemID;
			}
			else if (n1 >= 6)
			{
				rec.LC = (int)_floatBuf[0];
				rec.node = -1; //node id -1 for center
				rec.Sf = new float[8];

				rec.Sf[2] = _floatBuf[1]; // SQx
				rec.Sf[4] = _floatBuf[2]; // SQy
				rec.Sf[5] = _floatBuf[3]; // Mx
				rec.Sf[7] = _floatBuf[4]; // My
				rec.Sf[6] = _floatBuf[5]; // Mxy

				rec.elemID = _currentElementId;
			}
			else { return; }


			_currentLoadCase = rec.LC;

			int n2 = ExtractFloats(buf2, 0, len2, _floatBuf);
			if (n2 < 5) return;
			//				0	1	2	3	4
			//_floatBuf = [	~	~	Sx	Sy	Sxy]

			rec.Sf[0] = _floatBuf[2]; // Sx
			rec.Sf[3] = _floatBuf[3]; // Sy
			rec.Sf[1] = _floatBuf[4]; // Sxy
		}
		//Mutate stress record in place to lb / inch units
		UnitConvert.ConvertStressRecord(rec, _section.Force, _section.Length);
		rec.AssignMatrices();           //update matrices with float[] values
		rec.State = CoordFrame.Local;   //always local coordinates from STAAD
		// Safer thickness lookup;
		Element elem;
		if (_elems.TryGetValue(rec.elemID, out elem))
		{
			rec.t = elem.t;
			
			if (_targetState == CoordFrame.Reference)
			{
				rec.TransformToReference(_elems);
			}
			Stresses.Add(rec);
		}
		else
		{
			string msg = string.Format("Element {0} not found in ElementDict", rec.elemID);
			Console.WriteLine(msg);
			// throw new Exception();
			// Don't add to stressList
		}
		// rec.t = _elems[rec.elemID].t;   //assign thickness (already converted)
		
		
	}
}

public class AnlForceParser : AnlBlockParser
{
	public List<MemForce> Forces = new List<MemForce>();
	private int _currentMemberId;
	private int _currentLC;
	private Dictionary<int, Member> _mems;
	private CoordFrame _targetState;

	// true: the caller reads the print's units itself (AnlFrameReader), so the warning below is not printed
	public bool UnitsReadByCaller;

	public AnlForceParser(string path) : base(path) { }

	public void ParseSection(AnlSection section, Dictionary<int, Member> mems, CoordFrame targetState)
	{
		_mems = mems;
		_targetState = targetState;
		if (!UnitsReadByCaller)
			Console.WriteLine("WARNING: MEMBER FORCE UNIT NORMALIZATION NOT SUPPORTED");

		// One day we'll abstract this all away...
		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);

		_pb = new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);
			long endOffset = section.EndOffset;

			while (true)
			{
				//Console.WriteLine("0");
				CheckProgress();
				int len = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
				if (len <= 0) break;

				//Console.WriteLine("1");
				int n = ExtractFloats(_lineBuf1, 0, len, _floatBuf);
				int offset = 0;

				if (n == 9)
				{
					_currentMemberId = (int)_floatBuf[0];
					_currentLC = (int)_floatBuf[1];
					offset = 2;
				}
				else if (n == 8)
				{
					_currentLC = (int)_floatBuf[0];
					offset = 1;
				}
				else if (n == 7)
				{
					offset = 0;
				}
				else
				{
					continue;
				}

				//Console.WriteLine("2");
				var rec = new MemForce();
				rec.memID = _currentMemberId;
				rec.LC = _currentLC;
				rec.nodeID = (int)_floatBuf[offset];

				//Console.WriteLine("3");
				rec.Ff = new float[6];

				for (int i = 0; i < 6; i++) { rec.Ff[i] = _floatBuf[offset + i + 1]; }

				// TODO convert units
				rec.AssignMatrices();
				rec.State = CoordFrame.Local;   //always local coordinates from STAAD
				if (_targetState == CoordFrame.Reference)
				{
					rec.TransformToReference(_mems);
				}

				//Console.WriteLine("5");
				Forces.Add(rec);
			}
		}
		_pb.Finish();
	}
}

public class AnlDispParser : AnlBlockParser
{
	public List<Disp> Displacements = new List<Disp>();
	private int _currentNodeId;

	public AnlDispParser(string path) : base(path) { }

	public void ParseSection(AnlSection section)
	{
		// This should be in the constructor, but problem for later
		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);

		_pb = new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			while (true)
			{
				CheckProgress();
				int len = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
				if (len == -1) break;
				//Console.WriteLine("len = '{0}', line = '{1}'", len, Encoding.UTF8.GetString(_lineBuf1, 0, len));

				int n = ExtractFloats(_lineBuf1, 0, len, _floatBuf);

				int off = 0;
				// Line with node id + load, store the node id
				if (n >= 8)
				{
					_currentNodeId = (int)_floatBuf[0];
					off = 1;
				}
				// Expect minimum 6 disp/rot + load #
				if (n < 7)
				{
					continue;
				}

				var rec = new Disp();
				rec.node = _currentNodeId;
				rec.LC = (int)_floatBuf[off];
				rec.DR = new float[6];
				for (int i = 0; i < 6; i++)
				{
					// No conversion, always inches
					rec.DR[i] = (float)_floatBuf[off + 1 + i];
				}
				Displacements.Add(rec);
			}
			_pb.Finish();
		}
	}
}

public class AnlNodeParser : AnlBlockParser
{
	// Results
	public List<Node> Nodes = new List<Node>();
	// set (StaadInput): each joint's coordinates as written as well, doubles in the file's units
	public Dictionary<int, double[]> Exact;
	private readonly double[] _doubleBuf = new double[20];
	public AnlNodeParser(string path) : base(path) { }
	private AnlSection _section;

	public void ParseSection(AnlSection section)
	{
		_section = section;
		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);

		_pb = Quiet ? null : new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse joint data
			ParseNodeBlock(endOffset);

			if (_pb != null) _pb.Finish();
		}
	}

	public Dictionary<int, Node> GetNodeDict()
	{
		return Nodes.ToDictionary(n => n.id);
	}

	// Entry for joint coordinate data
	private void ParseNodeBlock(long endOffset)
	{
		// Skip and read functions respect the endOffset of this chunk of data
		// and return null when we reach the end of the data block
		while (true)
		{
			CheckProgress();
			// 1. Skip to next line starting with a number or '-'
			int len1 = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
			if (len1 == -1) break;
			// Console.WriteLine("len1: {0}, buf: {1}", len1, Encoding.UTF8.GetString(_lineBuf1, 0, len1));

			// 2. Parse joint data
			int n1 = ExtractFloats(_lineBuf1, 0, len1, _floatBuf);
			// Console.WriteLine("n1: {0}, buf: {1}", n1, _floatBuf[0]);

			byte lineNumOffset;

			//If we have two nodes in one line, the second won't have the line number in front of it
			if (n1 == 5) { lineNumOffset = 1; } else if (n1 == 4) { lineNumOffset = 0; } else continue;

			// 3. Assemble node object
			if ((n1 == 5) || (n1 == 4))
			{
				Node rec = new Node();
				//_floatBuf[0] is line number when present, otherwise everything shifts by one
				rec.id = (int)_floatBuf[lineNumOffset];
				rec.xyz = new Vector3(
					_floatBuf[lineNumOffset + 1],
					_floatBuf[lineNumOffset + 2],
					_floatBuf[lineNumOffset + 3]
				);
				
				// Convert to inches
				rec.xyz.X = (float)UnitConvert.ToInch(rec.xyz.X, _section.Length);
				rec.xyz.Y = (float)UnitConvert.ToInch(rec.xyz.Y, _section.Length);
				rec.xyz.Z = (float)UnitConvert.ToInch(rec.xyz.Z, _section.Length);
				Nodes.Add(rec);
				if (Exact != null)
				{
					ExtractDoubles(_lineBuf1, 0, len1, _doubleBuf);
					Exact[rec.id] = new double[] { _doubleBuf[lineNumOffset + 1], _doubleBuf[lineNumOffset + 2], _doubleBuf[lineNumOffset + 3] };
				}
			}
		}
	}
}

public class AnlGeomParser : AnlBlockParser
{
	public List<Member> Members = new List<Member>();
	public List<Element> Elements = new List<Element>();
	public readonly List<int> NoThickness = new List<int>();     // Quiet: elements with no thickness, not printed
	private Dictionary<int, Node> _nodes;
	private Dictionary<int, float> _thickness;
	private Dictionary<int, float[]> _memberSections;

	public AnlGeomParser(string path) : base(path) { }

	// NOT USED
	public Dictionary<int, Element> GetElementDict()
	{
		return Elements.ToDictionary(n => n.id);
	}

	// NOT USED
	public Dictionary<int, Member> GetMemberDict()
	{
		return Members.ToDictionary(n => n.id);
	}

	public void ParseElementSection(AnlSection section, Dictionary<int, Node> nodes, Dictionary<int, float> thickness)
	{
		_nodes = nodes;
		_thickness = thickness;

		//One day this will be in an abstract wrapper...
		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);
		_pb = Quiet ? null : new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse element data
			ParseElementBlock(endOffset);

			if (_pb != null) _pb.Finish();
		}
	}

	public void ParseMemberSection(AnlSection section, Dictionary<int, Node> nodes, Dictionary<int, float[]> memberSections)
	{
		_nodes = nodes;
		_memberSections = memberSections;

		//One day this will be in an abstract wrapper...
		long bytesToRead = section.EndOffset - section.StartOffset;
		float bytesMB = (float)(bytesToRead / (1L * 1024 * 1024));
		_progressInterval = 10L * 1024 * 1024;
		_nextProgressAt = section.StartOffset + _progressInterval;
		int totalTicks = (int)(bytesToRead / _progressInterval) + 1;
		string title = string.Format("Parsing section: {0} ({1:F2}MB)", section.Name, bytesMB);
		_pb = Quiet ? null : new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse member data
			ParseMemberBlock(endOffset);

			if (_pb != null) _pb.Finish();
		}
	}

	private void ParseElementBlock(long endOffset)
	{
		// Skip and read functions respect the endOffset of this chunk of data
		// and return null when we reach the end of the data block
		while (true)
		{
			CheckProgress();
			// 1. Skip to next line starting with a number or '-'
			int len1 = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
			if (len1 == -1) break;
			//Console.WriteLine("len1: {0}, buf: {1}", len1, Encoding.UTF8.GetString(_lineBuf1, 0, len1));

			// 2. Parse element data
			int n1 = ExtractFloats(_lineBuf1, 0, len1, _floatBuf);
			//Console.WriteLine("n1: {0}, buf: {1}", n1, _floatBuf[0]);

			//If we have two nodes in one line, the subsequent ones won't have the line number in front of it
			// But we can't check by number of values because
			//line#, id, n1, n2, n3 == 5 AND id, n1, n2, n3, n4 == 5 too
			int lineNumOffset = FirstTokenHasDot(_lineBuf1, len1) ? 1 : 0;

			// 3. Assemble element object
			if ((n1 == 4) || (n1 == 5) || (n1 == 6))
			{
				Element rec = new Element();
				//_floatBuf[0] should be line number
				rec.id = (int)_floatBuf[lineNumOffset];

				rec.nNodes = (byte)(n1 - lineNumOffset - 1); //total floats - line number (if present) - element id
															 //rec.t = _thickness[rec.id]; // Lookup thickness
				float t;
				if (!_thickness.TryGetValue(rec.id, out t))
				{
					// throw new KeyNotFoundException(string.Format("Element {0} has no thickness in _thickness dictionary (which is of size {1}.)", rec.id, _thickness.Count));
					if (Quiet) NoThickness.Add(rec.id);
					else Console.WriteLine(string.Format("Element {0} has no thickness in _thickness dictionary (which is of size {1}.)", rec.id, _thickness.Count));
					t = 0;
				}
				rec.t = t;
				rec.n = new Node[4];
				for (int i = 0; i < rec.nNodes; i++)
				{
					Node n = new Node();
					n.id = (int)_floatBuf[i + lineNumOffset + 1]; //skip the line # (if present) and element #
					n.xyz = _nodes[n.id].xyz; //Pass by value because Vector3 is a struct
					rec.n[i] = n;
				}
				rec.ComputeRotationMat();
				Elements.Add(rec);
			}
		}
	}

	private void ParseMemberBlock(long endOffset)
	{
		while (true)
		{
			CheckProgress();
			// 1. Skip to next line starting with a number or '-'
			int len1 = SkipToNextDataLine(endOffset, _lineBuf1, acceptJ: false);
			if (len1 == -1) break;
			//Console.WriteLine("len1: {0}, buf: {1}", len1, Encoding.UTF8.GetString(_lineBuf1, 0, len1));

			// 2. Parse member data
			int n1 = ExtractFloats(_lineBuf1, 0, len1, _floatBuf);
			//Console.WriteLine("n1: {0}, buf: {1}", n1, _floatBuf[0]);

			int lineNumOffset = FirstTokenHasDot(_lineBuf1, len1) ? 1 : 0;

			// 3. Assemble member object
			if ((n1 == 3) || (n1 == 4))
			{
				Member rec = new Member();
				//_floatBuf[0] should be line number
				rec.id = (int)_floatBuf[lineNumOffset];

				rec.n = new Node[2];
				for (int i = 0; i < 2; i++)
				{
					Node n = new Node();
					n.id = (int)_floatBuf[i + lineNumOffset + 1]; //skip the line # (if present) and member #
					n.xyz = _nodes[n.id].xyz; //Pass by value because Vector3 is a struct
					rec.n[i] = n;
				}
				rec.ComputeRotationMat();

				// Assign property if a prismatic section was defined
				float[] sect;
				if (_memberSections.TryGetValue(rec.id, out sect))
					rec.sect = sect;

				Members.Add(rec);
			}
		}
	}

	private static bool FirstTokenHasDot(byte[] buf, int len)
	{
		// Between the first non-space and the first following space, is there a dot?
		int i = 0;
		if (len <= 0) return false;
		while (i < len && (buf[i] == (byte)' ' || buf[i] == (byte)'\t'))
		{
			i++;
		}

		while (i < len && buf[i] != (byte)' ' && buf[i] != (byte)'\t')
		{
			if (buf[i] == (byte)'.') return true;
			i++;
		}
		return false;
	}

}

// Text based parsers for the short complex sections
public class AnlGroupParser
{
	public Dictionary<string, List<int>> Groups;
	public Dictionary<int, float> Thickness;
	public Dictionary<int, float[]> MemberSections;
	public Dictionary<int, string> LoadCaseNames = new Dictionary<int, string>();
	public Dictionary<string, string> GroupKinds = new Dictionary<string, string>();   // group -> the kind line above it (JOINT, MEMBER, ELEMENT ...)
	public List<string> GroupOrder = new List<string>();                              // groups as defined
	public Dictionary<int, double> ThicknessExact = new Dictionary<int, double>();    // as written, in the file's units
	private string _path;

	public AnlGroupParser(string path)
	{
		_path = path;
		Groups = new Dictionary<string, List<int>>();
		MemberSections = new Dictionary<int, float[]>(); //Member id -> [YD, ZD]
		Thickness = new Dictionary<int, float>();
	}

	public void ParseGroups(AnlSection section)
	{
		string[] tokens = Tokenize(ReadAndCleanSection(section));

		int i = 0;
		string kind = null;
		while (i < tokens.Length)
		{
			if (tokens[i].StartsWith("_"))
			{
				// Console.WriteLine("Found a group! {0}",tokens[i]);
				string name = tokens[i];
				i++;
				Groups[name] = ParseIdRange(tokens, ref i);
				if (kind != null) GroupKinds[name] = kind;
				if (!GroupOrder.Contains(name)) GroupOrder.Add(name);
				// Console.WriteLine("Added {0} ids to group: {1}",Groups[name].Count,name);
			}
			else
			{
				string up = tokens[i].ToUpperInvariant();
				if (up == "JOINT" || up == "MEMBER" || up == "ELEMENT" || up == "SOLID" || up == "GEOMETRY" || up == "FLOOR")
					kind = up;
				i++;
			}
		}
	}

	public void ParseThickness(AnlSection section)
	{
		string[] tokens = Tokenize(ReadAndCleanSection(section));
		var pendingIds = new List<int>();
		int i = 0;

		while (i < tokens.Length)
		{
			if (tokens[i].Equals("THICKNESS", StringComparison.OrdinalIgnoreCase))
			{
				i++;
				float t;
				float.TryParse(tokens[i], System.Globalization.NumberStyles.Float,
								System.Globalization.CultureInfo.InvariantCulture, out t);
				double exact;
				double.TryParse(tokens[i], System.Globalization.NumberStyles.Float,
								System.Globalization.CultureInfo.InvariantCulture, out exact);
				// Convert to inches
				t = (float)UnitConvert.ToInch(t, section.Length);
				i++;
				foreach (int id in pendingIds)
				{
					Thickness[id] = t;
					ThicknessExact[id] = exact;
				}

				pendingIds.Clear();
			}
			else if (tokens[i].StartsWith("_"))
			{
				string groupName = tokens[i].ToUpperInvariant();
				i++;
				List<int> groupIds;
				if (Groups.TryGetValue(groupName, out groupIds))
					pendingIds.AddRange(groupIds);
			}
			else
			{
				List<int> ids = ParseIdRange(tokens, ref i);

				if (ids.Count > 0)
				{
					pendingIds.AddRange(ids);
				}
				else
				{
					i++;
				}
			}
		}
	}

	// TODO UNITS
	public void ParseMemberProperties(AnlSection section)
	{
		string[] tokens = Tokenize(ReadAndCleanSection(section));
		var pendingIds = new List<int>();
		int i = 0;

		while (i < tokens.Length)
		{
			if (tokens[i].StartsWith("_"))
			{
				string groupName = tokens[i].ToUpperInvariant();
				i++;
				pendingIds.Clear();
				List<int> groupIds;
				if (Groups.TryGetValue(groupName, out groupIds))
					pendingIds.AddRange(groupIds);

			}
			else if (tokens[i].Equals("PRIS", StringComparison.OrdinalIgnoreCase))
			{
				i++;
				float yd = 0;
				float zd = 0;

				// Parse key-value pairs until next group or end
				while (i < tokens.Length && !tokens[i].StartsWith("_"))
				{
					if (tokens[i].Equals("YD", StringComparison.OrdinalIgnoreCase))
					{
						i++;
						// Console.WriteLine("tokens[{0}]: {1}; tokens[{2}]: {3}",i-1,tokens[i-1],i,tokens[i]);
						float.TryParse(tokens[i], System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out yd);
					}
					else if (tokens[i].Equals("ZD", StringComparison.OrdinalIgnoreCase))
					{
						i++;
						float.TryParse(tokens[i], System.Globalization.NumberStyles.Float,
							System.Globalization.CultureInfo.InvariantCulture, out zd);
					}
					i++;
				}
				
				yd = (float)UnitConvert.ToInch(yd, section.Length);
				zd = (float)UnitConvert.ToInch(zd, section.Length);
				
				foreach (int id in pendingIds)
					MemberSections[id] = new float[] { yd, zd };
			}
			else
			{
				// Not PRIS - skip until next group name
				while (i < tokens.Length && !tokens[i].StartsWith("_"))
					i++;
			}
		}
	}

	public void ParseLoadCaseNames(AnlSection section)
	{
		long length = section.EndOffset - section.StartOffset;
		byte[] buf = new byte[length];

		using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			fs.Seek(section.StartOffset, SeekOrigin.Begin);
			fs.Read(buf, 0, (int)length);
		}

		string text = Encoding.UTF8.GetString(buf);
		string[] lines = text.Split(new[] { '\r', '\n' },
							StringSplitOptions.RemoveEmptyEntries);
		string line = lines[0]; //Every "LOAD" section is a single line followed by stuff we don't want

		int dotIdx = line.IndexOf('.');
		if (dotIdx < 0) { return; }
		;

		string after = line.Substring(dotIdx + 1).TrimStart();

		// Extract LC #, must be properly numeric (skip R1 and similar)
		string rest;
		if (after.StartsWith("LOAD COMB "))
		{
			rest = after.Substring(10).TrimStart(); // Skip the word "LOAD COMB "
		}
		else if (after.StartsWith("LOAD COMBINATION "))
		{
			rest = after.Substring(17).TrimStart(); // Skip the word "LOAD COMBINATION "
		}
		else if (after.StartsWith("LOAD "))
		{
			rest = after.Substring(5).TrimStart(); // Skip the word "LOAD "
		}
		else { return; }

		int numEnd = 0;
		while (numEnd < rest.Length && rest[numEnd] >= '0' && rest[numEnd] <= '9') { numEnd++; }
		if (numEnd == 0) { return; }

		// This will throw an error if we managed to extract a non-number
		int lcId = int.Parse(rest.Substring(0, numEnd));
		string name = rest.Substring(numEnd).Trim();

		LoadCaseNames[lcId] = name;
	}

	// Hacked together solution for parsing supports and returning a
	// "JointStresses" dictionary for plotting in the visualizer. Soil
	// springs are encoded as displacements and stresses are stored as
	// zero to avoid needing an overload method in the viewer writer.
	public Dictionary<int, Dictionary<int, float[]>> ParseSupports(AnlSection section)
	{
		string text = ReadAndCleanSection(section);
		string[] rawLines = text.Split('\n');
		List<Disp> DispList = new List<Disp>();
		Dictionary<int, Dictionary<int, float[]>> JointStressesDict;

		// First pass: join continuation lines (lines ending with '-')
		List<string> lines = new List<string>();
		StringBuilder current = new StringBuilder();
		for (int i = 0; i < rawLines.Length; i++)
		{
			string trimmed = rawLines[i].TrimEnd();
			if (trimmed.EndsWith("-"))
			{
				current.Append(trimmed.Substring(0, trimmed.Length - 1));
				current.Append(' ');
			}
			else
			{
				current.Append(trimmed);
				lines.Add(current.ToString());
				current.Clear();
			}
		}

		if (current.Length > 0)
		{
			lines.Add(current.ToString());
		}

		foreach (string line in lines)
		{
			var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length == 0) continue;

			// Collect all leading integer tokens as node IDs, expanding TO ranges
			List<int> nodes = new List<int>();
			int idx = 0;
			while (idx < tokens.Length)
			{
				if (tokens[idx] == "TO")
				{
					int end = int.Parse(tokens[idx + 1]);
					int start = nodes[nodes.Count - 1] + 1;
					for (int n = start; n <= end; n++)
					{
						nodes.Add(n);
					}
					idx += 2;
				}
				else
				{
					int n;
					if (int.TryParse(tokens[idx], out n))
					{
						nodes.Add(n);
						idx++;
					}
					else
					{
						break;
					}
				}

			}

			if (nodes.Count == 0) continue;

			// Parse K-values from the remainder

			float[] DR = new float[6];

			for (int i = 1; i < tokens.Length; i++)
			{
				int slot;
				switch (tokens[i])
				{
					case "KFX": slot = 0; break;
					case "KFY": slot = 1; break;
					case "KFZ": slot = 2; break;
					case "KMX": slot = 3; break;
					case "KMY": slot = 4; break;
					case "KMZ": slot = 5; break;
					default: continue;
				}
				DR[slot] = float.Parse(tokens[++i]);
			}

			foreach (int node in nodes)
			{
				var disp = new Disp();
				disp.node = node;
				disp.DR = DR;
				disp.LC = 1;
				DispList.Add(disp);
			}
		}

		// DispDict = DispList
		// .GroupBy(d => d.node)
		// .ToDictionary(
		// g => g.Key,
		// g => g.ToDictionary(d => d.LC));


		JointStressesDict = BuildStressDict(DispList);

		return JointStressesDict;
	}

	// Helper for the above
	private Dictionary<int, Dictionary<int, float[]>> BuildStressDict(List<Disp> DispList)
	{
		var jointStresses = new Dictionary<int, Dictionary<int, float[]>>();
		var lcDict = new Dictionary<int, float[]>();
		int LC = DispList[0].LC;
		foreach (var disp in DispList)
		{
			var padded = new float[14];
			Array.Copy(disp.DR, 0, padded, 8, 6);
			lcDict[disp.node] = padded;
		}
		jointStresses[LC] = lcDict;
		return jointStresses;
	}

	// Helper Functions
	private string ReadAndCleanSection(AnlSection section)
	{
		long length = section.EndOffset - section.StartOffset;
		byte[] buf = new byte[length];
		using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			fs.Seek(section.StartOffset, SeekOrigin.Begin);
			fs.Read(buf, 0, (int)length);
		}
		
		string text = Encoding.UTF8.GetString(buf);
		
		string[] cleanSplit = text.Split('\n');
		var kept = new System.Collections.Generic.List<string>(cleanSplit.Length);
		foreach (string line in cleanSplit)
		{
			if (line.Length > 0 && (line.StartsWith(" \f") || line.StartsWith("  **WARNING")))
			{
				// Console.WriteLine(string.Format("Dropped FF or Warning line: {0}", line));
				continue;
			}
			kept.Add(line);
		}
		string clean = string.Join("\n", kept);
		
		clean = clean.Replace("-\r\n", " ").Replace("-\n", " ");
		
		// drop entire lines that start with form feed (staad page break character)
		// clean = string.Join("\n",
			// clean.Split('\n').Where(line => !line.TrimStart().StartsWith("STAAD SPACE")));
			
		

		// Strip STAAD line numbers (digits followed by dot)
		clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\b\d+\.(?!\d)\s*", " ");
		
		// Console.WriteLine(string.Format("clean string: {0}",clean));
		return clean;
	}

	private string[] Tokenize(string clean)
	{
		return clean.Split(new[] { ' ', '\t', '\r', '\n' },
							StringSplitOptions.RemoveEmptyEntries);
	}

	// A section as STAAD reads its commands (a .std; numbered: an .anl's echo of its input): one entry per command, its
	// continuation lines (ending "-") joined, ";" separating commands, page breaks and warnings dropped; the comment lines
	// ("*") just before a command are kept with it. The section's own command is the first entry.
	public List<StaadCommand> Commands(AnlSection section, bool numbered)
	{
		long length = section.EndOffset - section.StartOffset;
		byte[] buf = new byte[length];
		using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			fs.Seek(section.StartOffset, SeekOrigin.Begin);
			fs.Read(buf, 0, (int)length);
		}
		var o = new List<StaadCommand>();
		var comments = new List<string>();
		var open = new StringBuilder();
		foreach (string raw in Encoding.UTF8.GetString(buf).Split('\n'))
		{
			if (raw.StartsWith(" \f") || raw.StartsWith("  **WARNING")) continue;
			string line = raw.TrimEnd('\r');
			if (numbered) line = LineNumber.Replace(line, "");
			line = line.Trim();
			if (line.Length == 0) continue;
			if (line[0] == '*') { comments.Add(line.Substring(1).Trim()); continue; }
			string[] parts = line.Split(';');
			for (int k = 0; k < parts.Length; k++)
			{
				string p = parts[k].Trim();
				if (k == parts.Length - 1 && p.EndsWith("-")) { open.Append(p.Substring(0, p.Length - 1)).Append(' '); continue; }
				open.Append(p);
				string text = open.ToString().Trim();
				open.Clear();
				if (text.Length == 0) continue;
				var c = new StaadCommand(text);
				c.Comments.AddRange(comments);
				comments.Clear();
				o.Add(c);
			}
		}
		if (open.ToString().Trim().Length > 0)
		{
			var c = new StaadCommand(open.ToString().Trim());
			c.Comments.AddRange(comments);
			o.Add(c);
		}
		return o;
	}

	private static readonly System.Text.RegularExpressions.Regex LineNumber = new System.Text.RegularExpressions.Regex(@"^\s*\d+\.(?!\d)\s*");

	private List<int> ParseIdRange(string[] tokens, ref int i)
	{
		var ids = new List<int>();
		int val = 0;
		while (i < tokens.Length)
		{
			//End condition is failing to parse an int or finding the next underscore
			bool startsUnderscore = tokens[i].StartsWith("_");
			bool isNumber = int.TryParse(tokens[i], out val);
			if (startsUnderscore || !isNumber)
			{
				break;
			}

			ids.Add(val);
			i++;

			if (i < tokens.Length && tokens[i].Equals("TO", StringComparison.OrdinalIgnoreCase))
			{
				i++;
				int to = int.Parse(tokens[i]);
				for (int r = ids[ids.Count - 1] + 1; r <= to; r++)
					ids.Add(r);
				i++;
			}
		}
		return ids;
	}

}

// ------ The input commands of a .std (2026-11-23) -------------

// One STAAD command, as AnlGroupParser.Commands reads it
public sealed class StaadCommand
{
	public readonly string Text;
	public readonly string[] Tokens;
	public readonly List<string> Comments = new List<string>();   // the comment lines just before it, "*" removed

	public StaadCommand(string text)
	{
		Text = text;
		Tokens = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
	}
}

// A command of the form most STAAD data takes: an id list, then what applies to it ("1 2 5 TO 9 PRIS AX 1 ...",
// "101 FIXED BUT MX", "5 START 0 0 6"); CONSTANTS turn it round ("BETA 90 MEMB 3": Spec BETA 90, Ids 3)
public sealed class StaadSpec
{
	public List<int> Ids = new List<int>();
	public bool All;                                  // CONSTANTS ... ALL
	public string[] Spec = new string[0];
	public string Table;                              // MEMBER PROPERTY's table, its block's (AMERICAN, 'US (AISC 2023).db3')
	public string Text;                               // as written
	public List<string> Comments = new List<string>();
}

// DEFINE MATERIAL's ISOTROPIC name and its lines (E, POISSON, G, DENSITY, ALPHA, DAMP; TYPE; STRENGTH FY .. FU ..)
public sealed class StaadMaterial
{
	public string Name;
	public readonly Dictionary<string, double> Values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
	public string Type;
	public readonly Dictionary<string, double> Strength = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
}

// A load: a reference load (LOAD R1 ...), a primary load (LOAD 1 ...) or a combination (LOAD COMBINATION [SRSS | ABS] n);
// its blocks in order (SELFWEIGHT, JOINT LOAD, MEMBER LOAD, TEMPERATURE LOAD, REFERENCE LOAD, SUPPORT DISPLACEMENT LOAD;
// a combination's factor lines as one COMBINATION block)
public sealed class StaadLoad
{
	public string Kind;                               // reference, primary, combination
	public string Id;                                 // R1, 1
	public string LoadType = "";                      // LOADTYPE's words
	public string Title = "";                         // TITLE's text (a combination: its name)
	public string Method = "";                        // a combination's SRSS / ABS
	public string Text;
	public readonly List<StaadBlock> Blocks = new List<StaadBlock>();
}

public sealed class StaadBlock
{
	public string Name;                               // SELFWEIGHT, JOINT LOAD ...
	public string[] Head = new string[0];             // the words after the block's own command (SELFWEIGHT Y -1: Y -1)
	public readonly List<StaadSpec> Lines = new List<StaadSpec>();
}

// The input commands of a .std as data: what the .anl reader already reads -- the index (with the .std's own markers,
// AnlIndexer.StdMarkers), units, joints, member and plate incidences, groups and plate thicknesses -- with its own parsers,
// and text parsers for the commands an .anl never needed (materials, member properties, constants, offsets, releases,
// trusses, supports, loads, the analysis). Syntax only, numbers as written: what a command means is the caller's
// (StdToFrame, the frame model). A command it does not take is listed in Unread as written, never dropped.
public sealed class StaadInput
{
	public string Path;
	public List<AnlSection> Sections;
	public readonly List<string> Header = new List<string>();          // comment lines before the first command, "*" removed
	public readonly List<string> Preamble = new List<string>();        // the commands before UNIT (STAAD SPACE, INPUT WIDTH)
	public readonly List<string> JobInformation = new List<string>();  // START JOB INFORMATION ... END JOB INFORMATION (STAAD's own, a record only)
	public readonly List<string[]> Units = new List<string[]>();       // each UNIT command's words after UNIT
	public readonly List<int> JointIds = new List<int>();              // as defined
	public readonly Dictionary<int, double[]> Joints = new Dictionary<int, double[]>();   // STAAD X Y Z as written
	public readonly List<int[]> Members = new List<int[]>();           // id, joint i, joint j
	public readonly List<int[]> Plates = new List<int[]>();            // id, its joints
	public readonly List<int> PlaneStress = new List<int>();
	public readonly List<int> Truss = new List<int>();
	public Dictionary<string, List<int>> Groups;
	public Dictionary<string, string> GroupKinds;
	public List<string> GroupOrder;
	public Dictionary<int, double> Thickness;                            // plate -> its thickness as written
	public readonly List<StaadMaterial> Materials = new List<StaadMaterial>();
	public string PropertyTable;                                        // MEMBER PROPERTY's table (AMERICAN)
	public readonly List<StaadSpec> MemberProperties = new List<StaadSpec>();
	public readonly List<StaadSpec> ElementProperties = new List<StaadSpec>();
	public readonly List<StaadSpec> Constants = new List<StaadSpec>();
	public readonly List<StaadSpec> Offsets = new List<StaadSpec>();
	public readonly List<StaadSpec> Releases = new List<StaadSpec>();
	public readonly List<StaadSpec> Supports = new List<StaadSpec>();
	public readonly List<StaadLoad> Loads = new List<StaadLoad>();
	public string Analysis;                                             // PERFORM ANALYSIS, PDELTA 30 ANALYSIS SMALLDELTA ...
	public bool Finished;                                               // FINISH
	public readonly List<string> Unread = new List<string>();           // "<command>" or "<section>: <command>"

	static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
	static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

	public static StaadInput Read(string path)
	{
		var s = new StaadInput { Path = path };
		s.Sections = AnlIndexer.IndexFile(path, false, AnlIndexer.StdMarkers);
		AnlUnitStamper.StampUnits(path, s.Sections);
		s.Head();
		var grp = new AnlGroupParser(path);
		// groups and thicknesses first: an id list may name a group, and plates take their thickness
		foreach (var sec in s.Sections) if (sec.Name == "START GROUP DEFINITION") grp.ParseGroups(sec);
		s.Groups = grp.Groups; s.GroupKinds = grp.GroupKinds; s.GroupOrder = grp.GroupOrder;
		foreach (var sec in s.Sections) if (sec.Name == "ELEMENT PROPERTY") grp.ParseThickness(sec);
		s.Thickness = grp.ThicknessExact;
		Dictionary<int, Node> nodes = null;
		StaadLoad load = null;
		bool reference = false;
		foreach (var sec in s.Sections)
		{
			var cmds = grp.Commands(sec, false);
			if (cmds.Count == 0) continue;
			var head = cmds[0];
			var rest = cmds.Skip(1).ToList();
			string name = sec.Name;
			switch (name)
			{
				case "UNIT": s.Units.Add(head.Tokens.Skip(1).ToArray()); s.None(name, rest); break;
				case "JOINT COORDINATES": nodes = s.JointsIn(sec, rest); break;
				case "MEMBER INCIDENCES": s.Incidences(sec, rest, nodes, true); break;
				case "ELEMENT INCIDENCES SHELL": s.Incidences(sec, rest, nodes, false); break;
				case "ELEMENT PLANE STRESS": s.IdLines(name, rest, s.PlaneStress); break;
				case "MEMBER TRUSS": s.IdLines(name, rest, s.Truss); break;
				case "START GROUP DEFINITION":
					foreach (var c in rest)
						if (!(c.Tokens.Length == 1 && (Is(c.Text, "JOINT") || Is(c.Text, "MEMBER") || Is(c.Text, "ELEMENT"))) && !(c.Tokens[0].StartsWith("_") && s.AllIds(c.Tokens, 1)))
							s.Unread.Add(name + ": " + c.Text);
					break;
				case "DEFINE MATERIAL START": s.MaterialsIn(rest); break;
				case "MEMBER PROPERTY":
				{
					// each block its own table: STAAD 2025 writes a block per database ('US (AISC 2023).db3')
					string table = string.Join(" ", head.Tokens.Skip(2).ToArray());
					if (s.PropertyTable == null) s.PropertyTable = table;
					int from = s.MemberProperties.Count;
					s.Specs(name, rest, s.MemberProperties);
					for (int k = from; k < s.MemberProperties.Count; k++) s.MemberProperties[k].Table = table;
					break;
				}
				case "ELEMENT PROPERTY": s.Specs(name, rest, s.ElementProperties); break;
				case "CONSTANTS": s.ConstantsIn(rest); break;
				case "MEMBER OFFSET": s.Specs(name, rest, s.Offsets); break;
				case "MEMBER RELEASE": s.Specs(name, rest, s.Releases); break;
				case "SUPPORTS": s.Specs(name, rest, s.Supports); break;
				case "END GROUP DEFINITION": case "END DEFINE MATERIAL": s.None(name, rest); break;
				case "DEFINE REFERENCE LOADS": reference = true; load = null; s.None(name, rest); break;
				case "END DEFINE REFERENCE LOADS": reference = false; load = null; s.None(name, rest); break;
				case "LOAD": load = s.LoadIn(head, rest, reference); break;
				case "SELFWEIGHT": case "JOINT LOAD": case "MEMBER LOAD": case "TEMPERATURE LOAD": case "REFERENCE LOAD": case "SUPPORT DISPLACEMENT LOAD":
					s.BlockIn(name, head, rest, load);
					break;
				case "PERFORM ANALYSIS": case "PDELTA":
					if (s.Analysis != null) s.Unread.Add(head.Text + " (a second analysis)");
					else s.Analysis = head.Text;
					s.Prints(name, rest);
					break;
				case "FINISH": s.Finished = true; break;
				default:
					if (name.StartsWith("PRINT")) { s.Prints(name, rest); break; }
					foreach (var c in cmds) s.Unread.Add(c.Text);
					break;
			}
			if (s.Finished) break;
		}
		return s;
	}

	// the comments and commands before the first section
	void Head()
	{
		long end = Sections.Count > 0 ? Sections[0].StartOffset : new FileInfo(Path).Length;
		byte[] buf = new byte[end];
		using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read)) fs.Read(buf, 0, (int)end);
		bool job = false;
		foreach (string raw in Encoding.UTF8.GetString(buf).Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0) continue;
			if (line[0] == '*') { Header.Add(line.Substring(1).Trim()); continue; }
			if (Is(line, "START JOB INFORMATION")) { job = true; continue; }
			if (job) { if (Is(line, "END JOB INFORMATION")) job = false; else JobInformation.Add(line); continue; }
			foreach (string p in line.Split(';')) if (p.Trim().Length > 0) Preamble.Add(p.Trim());
		}
		if (job) Unread.Add("START JOB INFORMATION (no END JOB INFORMATION)");
	}

	void None(string section, List<StaadCommand> rest)
	{
		foreach (var c in rest) Unread.Add(section + ": " + c.Text);
	}

	void Prints(string section, List<StaadCommand> rest)
	{
		foreach (var c in rest) if (!c.Tokens[0].Equals("PRINT", StringComparison.OrdinalIgnoreCase)) Unread.Add(section + ": " + c.Text);
	}

	// an id list as STAAD writes one: numbers, "a TO b" (and "BY c"), group names; i stops at the first other token
	public List<int> IdList(string[] t, ref int i)
	{
		var o = new List<int>();
		while (i < t.Length)
		{
			List<int> g;
			if (t[i].StartsWith("_") && Groups != null && (Groups.TryGetValue(t[i], out g) || Groups.TryGetValue(t[i].ToUpperInvariant(), out g))) { o.AddRange(g); i++; continue; }
			int v, to, by;
			if (!int.TryParse(t[i], NumberStyles.None, Inv, out v)) break;
			i++;
			if (i + 1 < t.Length && Is(t[i], "TO") && int.TryParse(t[i + 1], NumberStyles.None, Inv, out to))
			{
				i += 2;
				by = 1;
				if (i + 1 < t.Length && Is(t[i], "BY") && int.TryParse(t[i + 1], NumberStyles.None, Inv, out by) && by > 0) i += 2;
				else by = 1;
				for (int r = v; r <= to; r += by) o.Add(r);
				continue;
			}
			o.Add(v);
		}
		return o;
	}

	bool AllIds(string[] t, int from)
	{
		int i = from;
		IdList(t, ref i);
		return i == t.Length;
	}

	static bool Number(string s, out double v) { return double.TryParse(s, NumberStyles.Float, Inv, out v); }

	// JOINT COORDINATES: the .anl reader's joints, exact; every command "id x y z"
	Dictionary<int, Node> JointsIn(AnlSection sec, List<StaadCommand> rest)
	{
		var anp = new AnlNodeParser(Path) { Quiet = true, Exact = new Dictionary<int, double[]>() };
		anp.ParseSection(sec);
		foreach (var c in rest)
		{
			int id;
			bool ok = c.Tokens.Length == 4 && int.TryParse(c.Tokens[0], NumberStyles.None, Inv, out id) && c.Tokens.Skip(1).All(t => { double q; return Number(t, out q); });
			if (!ok) { Unread.Add("JOINT COORDINATES: " + c.Text); continue; }
			id = int.Parse(c.Tokens[0], Inv);
			double[] p;
			if (!anp.Exact.TryGetValue(id, out p)) { Unread.Add("JOINT COORDINATES: " + c.Text + " (across lines)"); continue; }
			if (Joints.ContainsKey(id)) { Unread.Add("JOINT COORDINATES: " + c.Text + " (joint " + id.ToString(Inv) + " again)"); continue; }
			Joints[id] = p;
			JointIds.Add(id);
		}
		var d = new Dictionary<int, Node>();
		foreach (var n in anp.Nodes) d[n.id] = n;
		return d;
	}

	// MEMBER INCIDENCES ("id i j") or ELEMENT INCIDENCES SHELL ("id j1 j2 j3 [j4]"): the .anl reader's incidences, each
	// command checked first (its joints defined)
	void Incidences(AnlSection sec, List<StaadCommand> rest, Dictionary<int, Node> nodes, bool members)
	{
		string name = sec.Name;
		bool ok = true;
		foreach (var c in rest)
		{
			int n = c.Tokens.Length, v;
			bool good = members ? n == 3 : n == 4 || n == 5;
			var ids = new List<int>();
			for (int k = 0; good && k < n; k++) { good = int.TryParse(c.Tokens[k], NumberStyles.None, Inv, out v); ids.Add(v); }
			if (good && (nodes == null || ids.Skip(1).Any(j => !nodes.ContainsKey(j)))) { Unread.Add(name + ": " + c.Text + " (a joint not defined)"); ok = false; continue; }
			if (!good) { Unread.Add(name + ": " + c.Text); ok = false; }
		}
		if (!ok) return;
		var geo = new AnlGeomParser(Path) { Quiet = true };
		if (members)
		{
			geo.ParseMemberSection(sec, nodes, new Dictionary<int, float[]>());
			foreach (var m in geo.Members) Members.Add(new[] { m.id, m.n[0].id, m.n[1].id });
		}
		else
		{
			var t = new Dictionary<int, float>();
			foreach (var kv in Thickness) t[kv.Key] = (float)kv.Value;
			geo.ParseElementSection(sec, nodes, t);
			foreach (var e in geo.Elements)
			{
				var p = new List<int> { e.id };
				for (int k = 0; k < e.nNodes; k++) p.Add(e.n[k].id);
				Plates.Add(p.ToArray());
			}
		}
	}

	void IdLines(string section, List<StaadCommand> rest, List<int> into)
	{
		foreach (var c in rest)
		{
			int i = 0;
			var ids = IdList(c.Tokens, ref i);
			if (i != c.Tokens.Length || ids.Count == 0) { Unread.Add(section + ": " + c.Text); continue; }
			into.AddRange(ids);
		}
	}

	// "ids spec": the id list, then the rest
	void Specs(string section, List<StaadCommand> rest, List<StaadSpec> into)
	{
		foreach (var c in rest)
		{
			int i = 0;
			var ids = IdList(c.Tokens, ref i);
			if (ids.Count == 0 || i == c.Tokens.Length) { Unread.Add(section + ": " + c.Text); continue; }
			into.Add(new StaadSpec { Ids = ids, Spec = c.Tokens.Skip(i).ToArray(), Text = c.Text, Comments = c.Comments });
		}
	}

	// CONSTANTS: "<spec> MEMB <ids>" or "<spec> ALL"
	void ConstantsIn(List<StaadCommand> rest)
	{
		foreach (var c in rest)
		{
			var t = c.Tokens;
			int k = Array.FindIndex(t, x => Is(x, "MEMB") || Is(x, "MEMBER") || Is(x, "ALL"));
			if (k < 1) { Unread.Add("CONSTANTS: " + c.Text); continue; }
			var sp = new StaadSpec { Spec = t.Take(k).ToArray(), Text = c.Text, Comments = c.Comments };
			if (Is(t[k], "ALL")) { sp.All = true; if (k != t.Length - 1) { Unread.Add("CONSTANTS: " + c.Text); continue; } }
			else
			{
				int i = k + 1;
				sp.Ids = IdList(t, ref i);
				if (i != t.Length || sp.Ids.Count == 0) { Unread.Add("CONSTANTS: " + c.Text); continue; }
			}
			Constants.Add(sp);
		}
	}

	void MaterialsIn(List<StaadCommand> rest)
	{
		StaadMaterial m = null;
		foreach (var c in rest)
		{
			var t = c.Tokens;
			double v;
			if (Is(t[0], "ISOTROPIC") && t.Length == 2) { m = new StaadMaterial { Name = t[1] }; Materials.Add(m); continue; }
			if (m == null) { Unread.Add("DEFINE MATERIAL: " + c.Text); continue; }
			if (t.Length == 2 && Number(t[1], out v) && new[] { "E", "POISSON", "G", "DENSITY", "ALPHA", "DAMP" }.Any(x => Is(t[0], x)) && !m.Values.ContainsKey(t[0])) { m.Values[t[0]] = v; continue; }
			if (t.Length == 2 && Is(t[0], "TYPE") && m.Type == null) { m.Type = t[1]; continue; }
			if (Is(t[0], "STRENGTH") && t.Length % 2 == 1)
			{
				bool ok = true;
				for (int k = 1; k + 1 < t.Length; k += 2) { if (!Number(t[k + 1], out v) || m.Strength.ContainsKey(t[k])) { ok = false; break; } m.Strength[t[k]] = v; }
				if (ok) continue;
			}
			Unread.Add("DEFINE MATERIAL " + m.Name + ": " + c.Text);
		}
	}

	// LOAD R1 LOADTYPE Dead TITLE DEAD; LOAD 1 LOADTYPE ... TITLE ...; LOAD COMBINATION [SRSS | ABS] n name (its factor lines
	// in the same section)
	StaadLoad LoadIn(StaadCommand head, List<StaadCommand> rest, bool reference)
	{
		var t = head.Tokens;
		var l = new StaadLoad { Text = head.Text };
		int k = 1;
		if (t.Length > 1 && (Is(t[1], "COMBINATION") || Is(t[1], "COMB")))
		{
			l.Kind = "combination";
			k = 2;
			if (k < t.Length && (Is(t[k], "SRSS") || Is(t[k], "ABS"))) l.Method = t[k++].ToUpperInvariant();
			int n;
			if (reference || k >= t.Length || !int.TryParse(t[k], NumberStyles.None, Inv, out n)) { Unread.Add(head.Text); foreach (var c in rest) Unread.Add(head.Text + ": " + c.Text); return null; }
			l.Id = t[k];
			l.Title = string.Join(" ", t.Skip(k + 1).ToArray());
			var b = new StaadBlock { Name = "COMBINATION" };
			foreach (var c in rest) b.Lines.Add(new StaadSpec { Spec = c.Tokens, Text = c.Text, Comments = c.Comments });
			l.Blocks.Add(b);
			Loads.Add(l);
			return null;
		}
		int num;
		bool refId = t.Length > 1 && t[1].Length > 1 && (t[1][0] == 'R' || t[1][0] == 'r') && int.TryParse(t[1].Substring(1), NumberStyles.None, Inv, out num);
		bool primary = t.Length > 1 && int.TryParse(t[1], NumberStyles.None, Inv, out num);
		if ((reference && !refId) || (!reference && !primary)) { Unread.Add(head.Text); foreach (var c in rest) Unread.Add(head.Text + ": " + c.Text); return null; }
		l.Kind = reference ? "reference" : "primary";
		l.Id = t[1];
		// LOADTYPE's words up to TITLE; TITLE's text as written (its spaces kept)
		int lt = Array.FindIndex(t, x => Is(x, "LOADTYPE")), ti = Array.FindIndex(t, x => Is(x, "TITLE"));
		if (lt == 2) l.LoadType = string.Join(" ", t.Skip(3).Take((ti < 0 ? t.Length : ti) - 3).ToArray());
		if (ti >= 2)
		{
			int at = head.Text.IndexOf(" TITLE ", StringComparison.OrdinalIgnoreCase);
			l.Title = at < 0 ? "" : head.Text.Substring(at + 7).Trim();
		}
		if ((lt >= 0 && lt != 2) || (ti >= 0 && ti < 2) || (lt < 0 && ti < 0 && t.Length > 2) || (lt < 0 && ti > 2)) Unread.Add(head.Text);
		foreach (var c in rest) Unread.Add(head.Text + ": " + c.Text);
		Loads.Add(l);
		return l;
	}

	void BlockIn(string name, StaadCommand head, List<StaadCommand> rest, StaadLoad load)
	{
		if (load == null) { Unread.Add(head.Text + " (not in a load)"); foreach (var c in rest) Unread.Add(name + ": " + c.Text); return; }
		var b = new StaadBlock { Name = name };
		int words = name.Split(' ').Length;
		b.Head = head.Tokens.Skip(words).ToArray();
		if (name == "SELFWEIGHT") { foreach (var c in rest) Unread.Add(name + ": " + c.Text); }
		else if (name == "REFERENCE LOAD") { foreach (var c in rest) b.Lines.Add(new StaadSpec { Spec = c.Tokens, Text = c.Text, Comments = c.Comments }); }
		else
		{
			if (b.Head.Length > 0) Unread.Add(head.Text);
			Specs(name, rest, b.Lines);
		}
		load.Blocks.Add(b);
	}
}

public static class FirstBytes
{
	public static void Write(string inputFile, string outputFile, long byteCount)
	{
		using (var inStream = File.OpenRead(inputFile))
		using (var outStream = File.Create(outputFile))
		{
			var buffer = new byte[81920];
			long remaining = byteCount;
			while (remaining > 0)
			{
				int read = inStream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
				if (read == 0) break;
				outStream.Write(buffer, 0, read);
				remaining -= read;
			}
		}
	}
}
