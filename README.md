# ProfoundVifReader WIP

A command-line tool for converting Profound VIBRA VIF files to CSV format.
Its not ready
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

```bash
ProfoundVifReader <vif-file> [output.csv]
```

### Arguments

- `<vif-file>` - Path to the input VIF file (required)
- `[output.csv]` - Path to the output CSV file (optional, defaults to console output)

### Examples

Convert a VIF file and save to CSV:
```bash
ProfoundVifReader data.vif output.csv
```

Output to console:
```bash
ProfoundVifReader data.vif
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

```bash
dotnet build
```

## License

[Add your license information here]

## Author

[Add your name/organization here]

