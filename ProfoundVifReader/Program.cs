using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProfoundVifReader;

internal class Program
{
    // Command-line options
    private static bool OptionHeader = false;
    private static bool OptionNumber = true;
    private static bool OptionToday = false;
    private static bool OptionLong = false;
    private static string? OptionDateFilter = null; // "YYYY-MM-DD"

    private static void Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.OutputEncoding = Encoding.GetEncoding(1252);
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        List<string> inputFiles = new List<string>();

        // Parse command-line arguments
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "-V" || arg == "--version")
            {
                Console.WriteLine("(c) Copyright 2019-2025");
                Console.WriteLine("Profound VIF2CSV 1.10");
                Environment.Exit(0);
            }
            else if (arg == "-h" || arg == "--header")
            {
                OptionHeader = true;
                Log("set header on.");
            }
            else if (arg == "-D" || arg == "--today")
            {
                OptionToday = true;
                Log("filter: today only.");
            }
            else if (arg == "-L" || arg == "--long")
            {
                OptionLong = true;
                Log("long format enabled.");
            }
            else if (arg == "-N")
            {
                OptionNumber = false;
            }
            else if (arg == "-n")
            {
                OptionNumber = true;
            }
            else if (arg == "-d" || arg == "--day")
            {
                if (i + 1 >= args.Length)
                {
                    Console.Error.WriteLine("Option -d requires an argument");
                    PrintUsage();
                    return;
                }
                string dateStr = args[++i];
                Log($"set date filter to: \"{dateStr}\"");

                if (!ValidateDateString(dateStr))
                {
                    Console.Error.WriteLine("ERROR: invalid date format. Use YYYY-MM-DD or YY-MM-DD");
                    PrintUsage();
                    return;
                }
                OptionDateFilter = NormalizeDateString(dateStr);
            }
            else if (arg.StartsWith("-"))
            {
                Console.WriteLine($"Unknown Option '{arg}'");
                PrintUsage();
                return;
            }
            else
            {
                inputFiles.Add(arg);
            }
        }

        if (inputFiles.Count == 0)
        {
            PrintUsage();
            return;
        }

        try
        {
            // Process all input files
            bool firstFile = true;
            foreach (string file in inputFiles)
            {
                if (!File.Exists(file))
                {
                    Console.Error.WriteLine($"ERROR: can't open file: \"{file}\"");
                    continue;
                }

                var reader = new VifReader(OptionHeader && firstFile, OptionNumber, OptionToday, OptionLong, OptionDateFilter);
                reader.ProcessVifFile(file, null);
                firstFile = false;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static void Log(string msg)
    {
        Console.Error.WriteLine(msg);
    }

    private static void PrintUsage()
    {
        Console.WriteLine();
        Console.WriteLine("Profound Tool Suite");
        Console.WriteLine("use: VIF2CSV [OPTIONS] ... [FILES] ...");
        Console.WriteLine("OPTIONS:");
        Console.WriteLine(" -h  --header           = set header data first");
        Console.WriteLine(" -V  --version          = displays the version of this software");
        Console.WriteLine(" -n                     = add counter column (default: on)");
        Console.WriteLine(" -N                     = remove counter column");
        Console.WriteLine(" -d  --day \"YYYY-MM-DD\" = output only from a specified day");
        Console.WriteLine(" -D  --today            = output only from today");
        Console.WriteLine(" -L  --long             = export to csv with extended precision");
        Console.WriteLine("FILES are one or more vif-files.");
    }

    private static bool ValidateDateString(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;

        if (s.Length == 8) // YY-MM-DD
        {
            if (s[2] != '-' || s[5] != '-') return false;
            return IsDigits(s, 0, 2) && IsDigits(s, 3, 2) && IsDigits(s, 6, 2);
        }
        if (s.Length == 10) // YYYY-MM-DD
        {
            if (s[4] != '-' || s[7] != '-') return false;
            return IsDigits(s, 0, 4) && IsDigits(s, 5, 2) && IsDigits(s, 8, 2);
        }
        return false;
    }

    private static bool IsDigits(string s, int start, int count)
    {
        for (int i = start; i < start + count; i++)
            if (!char.IsDigit(s[i])) return false;
        return true;
    }

    private static string NormalizeDateString(string s)
    {
        // Convert YY-MM-DD to YYYY-MM-DD
        if (s.Length == 8)
        {
            int year = int.Parse(s.Substring(0, 2));
            return $"{2000 + year:D4}-{s.Substring(3)}";
        }
        return s;
    }
}

internal class VifReader
{
    private TextWriter? output;
    private const float FLOAT16_DIVISOR = 1000000.0f;
    private const float INT16_DIVISOR = 2.0f;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private int recordsProcessed = 0;
    private int recordsSkipped = 0;
    private int recordCounter = 0;

    private readonly bool printHeader;
    private readonly bool printNumber;
    private readonly bool filterToday;
    private readonly bool longFormat;
    private readonly string? dateFilter;
    private bool isKbMode = false;

    public VifReader(bool printHeader, bool printNumber, bool filterToday, bool longFormat, string? dateFilter)
    {
        this.printHeader = printHeader;
        this.printNumber = printNumber;
        this.filterToday = filterToday;
        this.longFormat = longFormat;
        this.dateFilter = dateFilter;
    }

    public void ProcessVifFile(string filename, string? outputFile)
    {
        output = outputFile != null ? new StreamWriter(outputFile, false, Encoding.UTF8) : Console.Out;

        try
        {
            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                // First pass: detect KB mode
                isKbMode = DetectKbMode(reader);
                
                // Print headers if requested
                if (printHeader)
                {
                    PrintHeaderNames();
                    PrintHeaderUnits();
                }

                // Second pass: process records
                reader.BaseStream.Seek(0, SeekOrigin.Begin);
                ProcessRecords(reader);
            }

            Console.Error.WriteLine($"Processed: {recordsProcessed}, Skipped: {recordsSkipped}");
        }
        finally
        {
            if (outputFile != null) output?.Close();
        }
    }

    private bool DetectKbMode(BinaryReader reader)
    {
        var buffer = new byte[70];
        var state = 0;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var b = reader.ReadByte();

            if (state == 0 && b == 'V')
            {
                buffer[0] = b;
                state = 1;
            }
            else if (state == 1 && b == 'I')
            {
                buffer[1] = b;
                state = 2;
            }
            else if (state == 2 && b == 'B')
            {
                buffer[2] = b;
                state = 3;

                var bytesRead = reader.Read(buffer, 3, 9);
                if (bytesRead != 9)
                    break;

                var recordType = buffer[3];
                if (recordType == 0x8A)
                    return true;

                var recordSize = BitConverter.ToUInt16(buffer, 4);
                var remainingBytes = recordSize - 12;
                if (remainingBytes > 0)
                {
                    reader.BaseStream.Seek(remainingBytes, SeekOrigin.Current);
                }
                state = 0;
            }
            else
            {
                state = 0;
            }
        }
        return false;
    }

    private void PrintHeaderNames()
    {
        var outWriter = output!;
        StringBuilder sb = new StringBuilder();
        
        sb.Append("\"date\",\"time\",");
        if (printNumber) sb.Append("\"counter\",");
        
        sb.Append("\"state\",\"|v|\",");
        sb.Append(GetDirectionHeader("x"));
        sb.Append(GetDirectionHeader("y"));
        sb.Append(GetDirectionHeader("z"));
        
        sb.Append("\"temperature\",\"battery\",\"memory use\",\"usb powered\",");
        sb.Append("\"signal strength\",\"signal quality\",\"transmitted\",\"all transmitted\",");
        sb.Append("\"peak type\",\"code\",\"error code\",\"geophone\",\"clock changed\",");
        
        outWriter.WriteLine(sb.ToString());
    }

    private void PrintHeaderUnits()
    {
        var outWriter = output!;
        StringBuilder sb = new StringBuilder();
        
        sb.Append("\"YYYY-MM-DD\",\"hh:mm:ss\",");
        if (printNumber) sb.Append("\"count\",");
        
        sb.Append("\"\",\"mm/s\",");
        sb.Append(GetDirectionUnit());
        sb.Append(GetDirectionUnit());
        sb.Append(GetDirectionUnit());
        
        sb.Append("\"°C\",\"V\",\"%\",\"\",");
        sb.Append("\"dBm\",\"\",\"\",\"\",");
        sb.Append("\"\",\"\",\"\",\"\",\"\",");
        
        outWriter.WriteLine(sb.ToString());
    }

    private string GetDirectionHeader(string axis)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"\"state({axis})\",");
        sb.Append($"\"v({axis})\",");
        
        if (isKbMode) sb.Append($"\"kb({axis})\",");
        else sb.Append($"\"f_zc({axis})\",");

        sb.Append($"\"f_ft({axis})\",");
        sb.Append($"\"u({axis})\",");
        sb.Append($"\"a({axis})\",");
        sb.Append($"\"v_cat({axis})\",");
        sb.Append($"\"f_cat({axis})\",");
        return sb.ToString();
    }

    private string GetDirectionUnit()
    {
        return "\"\",\"mm/s\",\"Hz\",\"Hz\",\"mm\",\"m/s2\",\"mm/s\",\"Hz\",";
    }

    private void ProcessRecords(BinaryReader reader)
    {
        // Search for VIB headers byte-by-byte (original approach)
        var buffer = new byte[70]; // Max size for one record
        var recordCount = 0;
        var state = 0; // 0=looking for 'V', 1=found 'V', 2=found 'VI', 3=found 'VIB'
        byte[]? lastRecord = null;
        long lastRecordPos = -1;
        long currentStartPos = -1;

        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var pos = reader.BaseStream.Position;
            var b = reader.ReadByte();

            if (state == 0 && b == 'V')
            {
                buffer[0] = b;
                state = 1;
                currentStartPos = pos;
            }
            else if (state == 1 && b == 'I')
            {
                buffer[1] = b;
                state = 2;
            }
            else if (state == 2 && b == 'B')
            {
                buffer[2] = b;
                state = 3;

                // Read type (1 byte) + size (2 bytes) + datetime (6 bytes) = 9 bytes
                var bytesRead = reader.Read(buffer, 3, 9);
                if (bytesRead != 9)
                    break;

                // Get record size from bytes 4-5 (little-endian)
                var recordSize = BitConverter.ToUInt16(buffer, 4);

                // Read remaining data (recordSize - 12) bytes
                var remainingBytes = recordSize - 12;
                if (remainingBytes > 0)
                {
                    var maxRead = Math.Min(remainingBytes, buffer.Length - 12);
                    bytesRead = reader.Read(buffer, 12, maxRead);
                    if (bytesRead != maxRead)
                        break;
                    if (remainingBytes > maxRead)
                        reader.BaseStream.Seek(remainingBytes - maxRead, SeekOrigin.Current);
                }

                recordCount++;
                if (lastRecord != null && lastRecordPos >= 0)
                {
                    int readType = GetReadTypeFromDelta(currentStartPos - lastRecordPos);
                    ProcessRecord(lastRecord, readType);
                }
                if (lastRecord == null)
                {
                    lastRecord = new byte[buffer.Length];
                }
                Array.Copy(buffer, lastRecord, buffer.Length);
                lastRecordPos = currentStartPos;
                state = 0; // Reset to search for next VIB
            }
            else
            {
                state = 0; // Reset if pattern broken
            }
        }

        if (lastRecord != null)
        {
            ProcessRecord(lastRecord, 2);
        }

        Console.Error.WriteLine($"Total records: {recordCount}");
    }

    private int GetReadTypeFromDelta(long delta)
    {
        if (delta == 68)
            return 2;
        return 5;
    }

    private void ProcessRecord(byte[] record, int readType)
    {
        // VIB header is always present at offset 0-2 when we call this
        var recordType = record[3];
        var recordSize = BitConverter.ToUInt16(record, 4);

        if (!IsValidToProcess(recordSize, readType))
        {
            recordsSkipped++;
            return;
        }

        // Validate record type and size (match original tool)
        if ( (recordType & 0xFD) != 0x88 )
        {
            recordsSkipped++;
            return;
        }
        if (recordSize != 68)
        {
            recordsSkipped++;
            return;
        }

        // Parse datetime - VIB at 0-2, data starts at 3
        // Order at offset 6-11: [second][minute][hour][day][month][year]
        int second = record[6];
        int minute = record[7];
        int hour = record[8];
        int day = record[9];
        int month = record[10];
        var year = 2000 + record[11];

        if (!DateTimeValid(record[6], record[7], record[8], record[9], record[10], record[11]))
        {
            recordsSkipped++;
            return;
        }

        var date = $"{year:D4}-{month:D2}-{day:D2}";
        var time = $"{hour:D2}:{minute:D2}:{second:D2}";

        // Apply date filters
        if (filterToday)
        {
            var today = DateTime.Today;
            if (year != today.Year || month != today.Month || day != today.Day)
            {
                recordsSkipped++;
                return;
            }
        }
        else if (dateFilter != null)
        {
            if (date != dateFilter)
            {
                recordsSkipped++;
                return;
            }
        }

        recordsProcessed++;
        recordCounter++;

        var isExtendedRecord = recordType == 0x8A;

        // Counter at offset 64-66
        var counter = "";
        if (recordSize > 0x43 && record.Length >= 67)
        {
            var counterValue = record[64] + ((record[65] + (record[66] << 8)) << 8);
            counter = counterValue.ToString();
        }

        // Parse directional data at offsets 14, 28, 42
        var xStatus = AxisStatus(record, 14);
        var yStatus = AxisStatus(record, 28);
        var zStatus = AxisStatus(record, 42);
        var overallStatus = GetOverallStatus(xStatus, yStatus, zStatus);

        var xData = ParseDirectionData(record, 14, isExtendedRecord, xStatus);
        var yData = ParseDirectionData(record, 28, isExtendedRecord, yStatus);
        var zData = ParseDirectionData(record, 42, isExtendedRecord, zStatus);

        // Parse sensor data at offsets 56+
        var temperature = record[56] * 0.5 - 27.5;
        var voltage = record[57] * 0.01 + 2.45;
        var memoryUse = record[58] & 0x7F;
        var usbPowered = record[58] >> 7;

        var signalStrengthRaw = record[59] & 0x1F;
        var signalStrength = signalStrengthRaw != 0 ? (2 * signalStrengthRaw - 113).ToString() : "";
        var signalQuality = GetSignalQuality(signalStrengthRaw);

        var transmitted = (record[59] & 0x20) != 0 ? 1 : 0;
        var allTransmitted = (record[59] & 0x40) != 0 ? 1 : 0;

        var peakType = record[60] & 3;
        var peakTypeCat = GetPeakTypeCat(peakType);
        var codeFlag = (record[60] & 4) != 0;
        var code = codeFlag ? "SBR" : "DIN";

        int errorCode = record[61];
        var geophoneSn = BitConverter.ToUInt16(record, 62);
        var geophone = FormatGeophone(geophoneSn);
        var clockChanged = record[60] >> 6;

        // Calculate overall state and |v| at offset 12
        var overallState = StatusToString(overallStatus);
        var overallV = overallStatus == 0 ? FormatFloat16(BitConverter.ToUInt16(record, 12)) : "";

        // Output CSV row - matching exact column order from header
        var outWriter = output!;
        var sb = new StringBuilder(512);
        void AppendField(string value)
        {
            sb.Append('\"').Append(value).Append('\"').Append(',');
        }
        void AppendLast(string value)
        {
            sb.Append('\"').Append(value).Append('\"');
        }

        AppendField(date);
        AppendField(time);
        if (printNumber)
        {
            AppendField(counter);
        }
        AppendField(overallState);
        AppendField(overallV);
        // X axis
        AppendField(xData.state);
        AppendField(xData.v);
        AppendField(xData.kb);
        AppendField(xData.ft);
        AppendField(xData.u);
        AppendField(xData.a);
        AppendField(xData.cv);
        AppendField(xData.cf);
        // Y axis
        AppendField(yData.state);
        AppendField(yData.v);
        AppendField(yData.kb);
        AppendField(yData.ft);
        AppendField(yData.u);
        AppendField(yData.a);
        AppendField(yData.cv);
        AppendField(yData.cf);
        // Z axis
        AppendField(zData.state);
        AppendField(zData.v);
        AppendField(zData.kb);
        AppendField(zData.ft);
        AppendField(zData.u);
        AppendField(zData.a);
        AppendField(zData.cv);
        AppendField(zData.cf);
        // Sensor data
        string tempStr;
        string voltStr;
        if (longFormat)
        {
            tempStr = FormatFixedOdd(temperature, 4);
            voltStr = FormatFixedOdd(voltage, 4);
        }
        else
        {
            tempStr = FormatFixedOdd(temperature, 1);
            voltStr = FormatFixedOdd(voltage, 2);
        }
        AppendField(tempStr);
        AppendField(voltStr);
        AppendField(memoryUse.ToString());
        AppendField(usbPowered.ToString());
        AppendField(signalStrength);
        AppendField(signalQuality);
        AppendField(transmitted.ToString());
        AppendField(allTransmitted.ToString());
        AppendField(peakTypeCat);
        AppendField(code);
        AppendField(errorCode.ToString());
        AppendField(geophone);
        AppendLast(clockChanged.ToString());
        outWriter.WriteLine(sb.ToString());
    }

    private (string v, string kb, string ft, string u, string a, string cv, string cf, string state) ParseDirectionData(
        byte[] record, int offset, bool isExtended, int status)
    {
        if (status != 0)
        {
            var state = StatusToString(status);
            return ("", "", "", "", "", "", "", state);
        }

        var v_raw = BitConverter.ToUInt16(record, offset);
        var kbzc_raw = BitConverter.ToInt16(record, offset + 2);
        var ft_raw = BitConverter.ToInt16(record, offset + 4);
        var u_raw = BitConverter.ToUInt16(record, offset + 6);
        var a_raw = BitConverter.ToUInt16(record, offset + 8);
        var cv_raw = BitConverter.ToUInt16(record, offset + 10);
        var cf_raw = BitConverter.ToInt16(record, offset + 12);

        var v = FormatFloat16(v_raw);
        string kb_or_zc;

        if (isExtended)
        {
            // KB mode
            var intVal = SV_FromFloat16((short)kbzc_raw);
            var kb_val = Math.Sqrt(intVal) * 0.01;
            if (kb_val <= 0.1)
                kb_val = 0.0;
            if (longFormat)
                kb_or_zc = FormatFixedOdd(kb_val, 4);
            else
                kb_or_zc = FormatFixedOdd(kb_val, 2);
        }
        else
        {
            // ZC mode
            if (kbzc_raw <= 0)
            {
                kb_or_zc = "";
            }
            else
            {
                var zc = 1024.0 / kbzc_raw;
                if (longFormat)
                    kb_or_zc = FormatFixedOdd(zc, 4);
                else
                    kb_or_zc = FormatFixedOdd(zc, 2);
            }
        }

        var ft = FormatInt16(ft_raw);
        var u = FormatFloat16(u_raw);
        var a = FormatFloat16(a_raw);
        var cv = FormatFloat16(cv_raw);
        var cf = FormatInt16(cf_raw);

        return (v, kb_or_zc, ft, u, a, cv, cf, "");
    }

    private int AxisStatus(byte[] record, int offset)
    {
        int s = SV_IsValueValid(BitConverter.ToUInt16(record, offset));
        if (s == 0) s = SV_IsValueValid(BitConverter.ToUInt16(record, offset + 6));
        if (s == 0) s = SV_IsValueValid(BitConverter.ToUInt16(record, offset + 8));
        if (s == 0) s = SV_IsValueValid(BitConverter.ToUInt16(record, offset + 10));
        return s;
    }

    private int GetOverallStatus(int xStatus, int yStatus, int zStatus)
    {
        if (xStatus == int.MaxValue || yStatus == int.MaxValue || zStatus == int.MaxValue)
            return int.MaxValue;
        if (xStatus != 0) return xStatus;
        if (yStatus != 0) return yStatus;
        if (zStatus != 0) return zStatus;
        return 0;
    }

    private string StatusToString(int status)
    {
        return status switch
        {
            -1 => "DISCONNECTED",
            -2 => "DATA INVALID",
            -3 => "NO DATA",
            -4 => "NOT RESPONDING",
            int.MaxValue => "OVERLOAD",
            _ => ""
        };
    }

    private string FormatFloat16(ushort value)
    {
        var intVal = SV_FromFloat16((short)value);

        if (IsSpecialValue(intVal) || IsOverload(intVal))
            return "";

        float result = intVal / FLOAT16_DIVISOR;
        return longFormat
            ? FormatFixedOdd(result, 4)
            : FormatFixedOdd(result, 2);
    }

    private string FormatInt16(short value)
    {
        if (IsSpecialValue(value))
            return "";

        float result = value / INT16_DIVISOR;
        return longFormat
            ? FormatFixedOdd(result, 4)
            : FormatFixedOdd(result, 1);
    }

    private int SV_FromFloat16(short value)
    {
        int result = value;
        uint v2 = ((uint)(ushort)value >> 11) - 1;
        if (v2 <= 0x13)
        {
            int v3 = value & 0x7FF;
            v3 |= 0x800;
            return v3 << (int)v2;
        }
        return result;
    }

    private bool IsSpecialValue(int value)
    {
        return value >= -4 && value <= -1;
    }

    private bool IsOverload(int value)
    {
        return value > 99999999;
    }

    private int SV_IsValueValid(ushort value)
    {
        uint v1 = ((uint)value >> 11) - 1;
        int result;

        if (v1 > 0x13)
        {
            result = (short)value;
            if ((uint)(short)value < 0xFFFFFFFC)
                return 0;
        }
        else
        {
            int v2 = value & 0x7FF;
            v2 = (v2 & 0xFF) | (((v2 >> 8) | 8) << 8);
            result = v2 << (int)v1;
            if ((uint)(result + 4) > 3)
            {
                bool valid = result <= 99999999;
                result = int.MaxValue;
                if (valid)
                    return 0;
            }
        }
        return result;
    }

    private string GetSignalQuality(int signalStrengthRaw)
    {
        if (signalStrengthRaw == 0)
            return "Unknown";

        if (signalStrengthRaw > 23)
            return "Excellent";
        if (signalStrengthRaw > 15)
            return "Good";
        if (signalStrengthRaw > 7)
            return "Low";
        return "Bad";
    }

    private string FormatGeophone(ushort value)
    {
        ushort type = (ushort)(value & 0xC000);
        int number = value & 0x3FFF;

        return type switch
        {
            0x4000 => $"TDA{number:D5}",
            0x8000 => $"TDS{number:D5}",
            0xC000 => "???00000",
            _ => $"unknown{number:D5}"
        };
    }

    private bool DateTimeValid(byte second, byte minute, byte hour, byte day, byte month, byte year)
    {
        if (second > 59 || minute > 59 || hour > 23)
            return false;
        if (year > 99)
            return false;
        if (month < 1 || month > 12)
            return false;

        int maxDay;
        switch (month)
        {
            case 1:
            case 3:
            case 5:
            case 7:
            case 8:
            case 10:
            case 12:
                maxDay = 31;
                break;
            case 4:
            case 6:
            case 9:
            case 11:
                maxDay = 30;
                break;
            default:
                maxDay = 28;
                if ((year & 3) == 0)
                    maxDay = 29;
                break;
        }

        if (day < 1 || day > maxDay)
            return false;

        return true;
    }

    private string FormatFixedOdd(double value, int decimals)
    {
        double scale = Math.Pow(10.0, decimals);
        double scaled = value * scale;
        double sign = Math.Sign(scaled);
        double absScaled = Math.Abs(scaled);
        double floor = Math.Floor(absScaled);
        double frac = absScaled - floor;
        double rounded;

        const double tieEpsilon = 1e-7;
        if (frac > 0.5 + tieEpsilon)
        {
            rounded = floor + 1;
        }
        else if (frac < 0.5 - tieEpsilon)
        {
            rounded = floor;
        }
        else
        {
            rounded = ((long)floor % 2 == 0) ? floor + 1 : floor;
        }

        rounded *= sign;
        double result = rounded / scale;
        return result.ToString("F" + decimals, InvariantCulture);
    }

    private bool IsValidToProcess(int recordSize, int readType)
    {
        return readType <= 9 && recordSize == 68 && ((1 << readType) & 0x45) != 0;
    }


    private string GetPeakTypeCat(int peakType)
    {
        return peakType switch
        {
            0 => "vcatnone",
            1 => "vcat1",
            2 => "vcat2",
            3 => "vcat3",
            _ => "vcat"
        };
    }
}
