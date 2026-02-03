using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProfoundVifReader;

internal class Program
{
    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: ProfoundVifReader <vif-file> [output.csv]");
            Console.WriteLine("The vif2csv processes Profound VIBRA vif-files.");
            Console.WriteLine("The output is a CSV (Comma-Separated Values)");
            return;
        }

        var inputFile = args[0];
        var outputFile = args.Length > 1 ? args[1] : null;

        try
        {
            var reader = new VifReader();
            reader.ProcessVifFile(inputFile, outputFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Environment.Exit(1);
        }
    }
}

internal class VifReader
{
    private TextWriter output;
    private const float FLOAT16_DIVISOR = 1000000.0f;
    private const float INT16_DIVISOR = 2.0f;
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private static bool debugPrinted = false;
    private int recordsProcessed = 0;
    private int recordsSkipped = 0;

    public void ProcessVifFile(string filename, string outputFile)
    {
        output = outputFile != null ? new StreamWriter(outputFile, false, Encoding.UTF8) : Console.Out;

        try
        {
            using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(fs))
            {
                ProcessRecords(reader);
            }

            Console.Error.WriteLine($"Processed: {recordsProcessed}, Skipped: {recordsSkipped}");
        }
        finally
        {
            if (outputFile != null) output?.Close();
        }
    }

    private void ProcessRecords(BinaryReader reader)
    {
        // Search for VIB headers byte-by-byte (original approach)
        var buffer = new byte[70]; // Max size for one record
        var recordCount = 0;
        var state = 0; // 0=looking for 'V', 1=found 'V', 2=found 'VI', 3=found 'VIB'

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

                // Read type (1 byte) + size (2 bytes) + datetime (6 bytes) = 9 bytes
                var bytesRead = reader.Read(buffer, 3, 9);
                if (bytesRead != 9)
                    break;

                // Get record size from bytes 4-5 (little-endian)
                var recordSize = BitConverter.ToUInt16(buffer, 4);

                // Read remaining data (recordSize - 12) bytes
                var remainingBytes = recordSize - 12;
                if (remainingBytes > 0 && remainingBytes < 60)
                {
                    bytesRead = reader.Read(buffer, 12, remainingBytes);
                    if (bytesRead != remainingBytes)
                        break;
                }

                recordCount++;
                ProcessRecord(buffer);
                state = 0; // Reset to search for next VIB
            }
            else
            {
                state = 0; // Reset if pattern broken
            }
        }

        Console.Error.WriteLine($"Total records: {recordCount}");
    }

    private void ProcessRecord(byte[] record)
    {
        // VIB header is always present at offset 0-2 when we call this
        var recordType = record[3];
        var recordSize = BitConverter.ToUInt16(record, 4);

        recordsProcessed++;

        // Parse datetime - VIB at 0-2, data starts at 3
        // Order at offset 6-11: [second][minute][hour][day][month][year]
        int second = record[6];
        int minute = record[7];
        int hour = record[8];
        int day = record[9];
        int month = record[10];
        var year = 2000 + record[11];

        var date = $"{year:D4}-{month:D2}-{day:D2}";
        var time = $"{hour:D2}:{minute:D2}:{second:D2}";

        var isExtendedRecord = recordType == 0x8A;

        // Counter at offset 64-66
        var counter = "";
        if (recordSize > 0x43 && record.Length >= 67)
        {
            var counterValue = record[64] + ((record[65] + (record[66] << 8)) << 8);
            counter = counterValue.ToString();
        }

        // Parse directional data at offsets 14, 28, 42
        var xData = ParseDirectionData(record, 14, isExtendedRecord);
        var yData = ParseDirectionData(record, 28, isExtendedRecord);
        var zData = ParseDirectionData(record, 42, isExtendedRecord);

        // Parse sensor data at offsets 56+
        var temperature = record[56] * 0.5 - 27.5;
        var voltage = record[57] * 0.01 + 2.45;
        var memoryUse = record[58] & 0x7F;
        var usbPowered = record[58] >> 7;

        var signalStrengthRaw = record[59] & 0x1F;
        var signalStrength = signalStrengthRaw != 0 ? 2 * signalStrengthRaw - 113 : 0;
        var signalQuality = GetSignalQuality(signalStrengthRaw);

        var transmitted = (record[59] & 0x20) != 0 ? 1 : 0;
        var allTransmitted = (record[59] & 0x40) != 0 ? 1 : 0;

        var peakType = record[60] & 3;
        var peakTypeCat = GetPeakTypeCat(peakType);
        var codeFlag = (record[60] & 4) != 0;
        var code = codeFlag ? "DIN" : "ISO";

        int errorCode = record[61];
        var geophoneSn = BitConverter.ToUInt16(record, 62);
        var geophone = $"TDA{geophoneSn:D5}";
        var clockChanged = record[60] >> 6;

        // Calculate overall state and |v| at offset 12
        var overallState = xData.state;
        var overallV = FormatFloat16(BitConverter.ToUInt16(record, 12));

        // Output CSV row - matching exact column order from header
        output.Write($"\"{date}\",");
        output.Write($"\"{time}\",");
        output.Write($"\"{counter}\",");
        output.Write($"\"{overallState}\",");
        output.Write($"\"{overallV}\",");
        // X axis
        output.Write($"\"{xData.state}\",");
        output.Write($"\"{xData.v}\",");
        output.Write($"\"{xData.kb}\",");
        output.Write($"\"{xData.ft}\",");
        output.Write($"\"{xData.u}\",");
        output.Write($"\"{xData.a}\",");
        output.Write($"\"{xData.cv}\",");
        output.Write($"\"{xData.cf}\",");
        // Y axis
        output.Write($"\"{yData.state}\",");
        output.Write($"\"{yData.v}\",");
        output.Write($"\"{yData.kb}\",");
        output.Write($"\"{yData.ft}\",");
        output.Write($"\"{yData.u}\",");
        output.Write($"\"{yData.a}\",");
        output.Write($"\"{yData.cv}\",");
        output.Write($"\"{yData.cf}\",");
        // Z axis
        output.Write($"\"{zData.state}\",");
        output.Write($"\"{zData.v}\",");
        output.Write($"\"{zData.kb}\",");
        output.Write($"\"{zData.ft}\",");
        output.Write($"\"{zData.u}\",");
        output.Write($"\"{zData.a}\",");
        output.Write($"\"{zData.cv}\",");
        output.Write($"\"{zData.cf}\",");
        // Sensor data
        output.Write($"\"{temperature.ToString("F1", InvariantCulture)}\",");
        output.Write($"\"{voltage.ToString("F2", InvariantCulture)}\",");
        output.Write($"\"{memoryUse}\",");
        output.Write($"\"{usbPowered}\",");
        output.Write($"\"{signalStrength}\",");
        output.Write($"\"{signalQuality}\",");
        output.Write($"\"{transmitted}\",");
        output.Write($"\"{allTransmitted}\",");
        output.Write($"\"{peakTypeCat}\",");
        output.Write($"\"{code}\",");
        output.Write($"\"{errorCode}\",");
        output.Write($"\"{geophone}\",");
        output.WriteLine($"\"{clockChanged}\",");
    }

    private (string v, string kb, string ft, string u, string a, string cv, string cf, string state) ParseDirectionData(
        byte[] record, int offset, bool isExtended)
    {
        var v_raw = BitConverter.ToUInt16(record, offset);
        var kbzc_raw = BitConverter.ToInt16(record, offset + 2);
        var ft_raw = BitConverter.ToInt16(record, offset + 4);
        var u_raw = BitConverter.ToUInt16(record, offset + 6);
        var a_raw = BitConverter.ToUInt16(record, offset + 8);
        var cv_raw = BitConverter.ToUInt16(record, offset + 10);
        var cf_raw = BitConverter.ToInt16(record, offset + 12);

        // Determine state based on special values
        var state = "";
        var intV = SV_FromFloat16(v_raw);
        if (IsSpecialValue(intV))
            state = "";
        else if (IsOverload(intV))
            state = "Overload";

        var v = FormatFloat16(v_raw);
        string kb_or_zc;

        if (isExtended)
        {
            // KB mode
            var intVal = SV_FromFloat16(kbzc_raw);
            var kb_val = Math.Sqrt(intVal) * 0.01;
            kb_or_zc = kb_val > 0.1 ? kb_val.ToString("F2", InvariantCulture) : "0.00";
        }
        else
        {
            // ZC mode
            kb_or_zc = kbzc_raw > 0 ? (1024.0 / kbzc_raw).ToString("F2", InvariantCulture) : "";
        }

        var ft = FormatInt16(ft_raw);
        var u = FormatFloat16(u_raw);
        var a = FormatFloat16(a_raw);
        var cv = FormatFloat16(cv_raw);
        var cf = FormatInt16(cf_raw);

        return (v, kb_or_zc, ft, u, a, cv, cf, state);
    }

    private string FormatFloat16(ushort value)
    {
        var intVal = SV_FromFloat16(value);

        if (IsSpecialValue(intVal))
            return "";

        if (IsOverload(intVal))
            return "";

        double result = intVal / FLOAT16_DIVISOR;
        return result.ToString("F2", InvariantCulture);
    }

    private string FormatInt16(short value)
    {
        if (IsSpecialValue(value))
            return "";

        if (IsOverload(value))
            return "";

        double result = value / INT16_DIVISOR;
        return result.ToString("F1", InvariantCulture);
    }

    private int SV_FromFloat16(int value)
    {
        // Convert float16 to signed integer value
        var uval = (ushort)value;

        if (uval == 0 || uval == 0xFFFF)
            return 0;

        // Extract components
        var sign = (uval >> 15) & 1;
        var exponent = (uval >> 10) & 0x1F;
        var mantissa = uval & 0x3FF;

        if (exponent == 0)
        {
            if (mantissa == 0)
                return 0;
            // Denormalized
            var val = mantissa / 1024.0 * Math.Pow(2, -14);
            return (int)(val * 1000000 * (sign == 1 ? -1 : 1));
        }

        if (exponent == 31)
            // Special value
            return int.MaxValue;

        // Normalized
        var value_d = (1.0 + mantissa / 1024.0) * Math.Pow(2, exponent - 15);
        return (int)(value_d * 1000000 * (sign == 1 ? -1 : 1));
    }

    private bool IsSpecialValue(int value)
    {
        return value == 0 || value == int.MaxValue || value == int.MinValue;
    }

    private bool IsOverload(int value)
    {
        return value == int.MaxValue;
    }

    private string GetSignalQuality(int signalStrengthRaw)
    {
        if (signalStrengthRaw == 0)
            return "";

        if (signalStrengthRaw <= 10)
            return "Low";
        else if (signalStrengthRaw <= 20)
            return "Medium";
        else
            return "High";
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