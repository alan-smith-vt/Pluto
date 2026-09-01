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

	private static readonly HashSet<byte> InterestingFirstBytes = BuildInterestingBytes();

	private static HashSet<byte> BuildInterestingBytes()
	{
		var set = new HashSet<byte>();
		foreach (string marker in SectionMarkers)
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
		var sections = new List<AnlSection>();
		AnlSection current = null;
		long fileLength = new FileInfo(path).Length;
		const long progressInterval = 10L * 1024 * 1024; // 10MB
		int totalTicks = (int)(fileLength / progressInterval) + 1;
		long nextProgressAt = 0;
		ProgressBar pb = new ProgressBar(totalTicks, title: "ANL First Pass");

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
					pb.Tick();
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
				if (!InterestingFirstBytes.Contains(contentByte))
					continue;

				// Strings are expensive
				string trimmed = Encoding.UTF8.GetString(lineBuf, contentStart, lineLen - contentStart);

				// Check if this line is a known section
				string matchedSection = MatchSectionHeader(trimmed);

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
		pb.Finish();
		return sections;
	}

	private static string MatchSectionHeader(string trimmedLine)
	{
		string upper = trimmedLine.ToUpperInvariant();
		for (int i = 0; i < SectionMarkers.Length; i++)
		{
			if (upper.StartsWith(SectionMarkers[i]))
				return SectionMarkers[i];
		}
		return null;
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

		while (i < end && count < output.Length)
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

			int numStart = i;
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

			int numLen = i - numStart;
			if (numLen > 0 && seenDigit)
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
		}

		return count; // return count of numbers found
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

	public AnlForceParser(string path) : base(path) { }

	public void ParseSection(AnlSection section, Dictionary<int, Member> mems, CoordFrame targetState)
	{
		_mems = mems;
		_targetState = targetState;
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

		_pb = new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse joint data
			ParseNodeBlock(endOffset);

			_pb.Finish();
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
			}
		}
	}
}

public class AnlGeomParser : AnlBlockParser
{
	public List<Member> Members = new List<Member>();
	public List<Element> Elements = new List<Element>();
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
		_pb = new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse element data
			ParseElementBlock(endOffset);

			_pb.Finish();
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
		_pb = new ProgressBar(totalTicks, title: title);

		using (_fs = new FileStream(_path, FileMode.Open, FileAccess.Read,
									FileShare.Read, 1 << 20))
		{
			_fs.Seek(section.StartOffset, SeekOrigin.Begin);
			_reader = new ByteTrackingLineReader(_fs);

			long endOffset = section.EndOffset;

			// Parse member data
			ParseMemberBlock(endOffset);

			_pb.Finish();
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
					Console.WriteLine(string.Format("Element {0} has no thickness in _thickness dictionary (which is of size {1}.)", rec.id, _thickness.Count));
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
		while (i < tokens.Length)
		{
			if (tokens[i].StartsWith("_"))
			{
				// Console.WriteLine("Found a group! {0}",tokens[i]);
				string name = tokens[i];
				i++;
				Groups[name] = ParseIdRange(tokens, ref i);
				// Console.WriteLine("Added {0} ids to group: {1}",Groups[name].Count,name);
			}
			else
			{
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
				// Convert to inches
				t = (float)UnitConvert.ToInch(t, section.Length);
				i++;
				foreach (int id in pendingIds)
					Thickness[id] = t;

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
