# ProfoundVifReader

Status: **Finished**

Tags: `vif` `csv` `profond-vibra` `cli` `dotnet` `reverse-engineering`
## Description

ProfoundVifReader processes binary VIF files from Profound VIBRA vibration monitoring systems and converts them into readable CSV (Comma-Separated Values) format. The tool extracts vibration measurements, sensor data, and metadata from the proprietary VIF format.

## Features

- Parses VIF binary format with byte-level precision
- Extracts 3-axis vibration data (X, Y, Z)
- Processes sensor information (temperature, voltage, signal strength)
- Outputs structured CSV with comprehensive measurement data
- Handles multiple record types (standard and extended)
- Includes geophone identification and error codes

## Requirements

- .NET 10.0 or higher

## Usage

```
ProfoundVifReader [OPTIONS] ... [FILES] ...
```

### Options

- `-h`, `--header` = set header data first
- `-V`, `--version` = displays the version of this software
- `-n` = add counter column (default: on)
- `-N` = remove counter column
- `-d`, `--day "YYYY-MM-DD"` = output only from a specified day
- `-D`, `--today` = output only from today
- `-L`, `--long` = export to csv with extended precision

### Arguments

- `<vif-file>` - Path to the input VIF file (required)
- `[output.csv]` - Path to the output CSV file (optional, defaults to console output)

### Examples

Convert a VIF file and save to CSV:
```
ProfoundVifReader data.vif > output.csv
```

Output with header and day filter:
```
ProfoundVifReader --header --day "2018-02-07" data.vif > out.csv
```

## Output Format

The tool generates CSV output with the following columns:

- Date/Time information
- Counter
- Overall state and velocity
- X, Y, Z axis measurements (state, velocity, KB/ZC, frequency, displacement, acceleration, etc.)
- Sensor data (temperature, voltage, memory usage, USB power status)
- Signal information (strength, quality, transmission status)
- Peak type, code (ISO/DIN), error codes
- Geophone serial number
- Clock status

## Building

```
dotnet build
```

## License

MIT

