"""Fail the build when merged line coverage is below the minimum.

Usage: python3 check_coverage.py <merged Cobertura.xml> <minimum percent>
"""
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    path, minimum = sys.argv[1], float(sys.argv[2])
    rate = float(ET.parse(path).getroot().get("line-rate", "0")) * 100

    print(f"Line coverage: {rate:.1f}% (minimum {minimum:.0f}%)")
    if rate < minimum:
        print(f"::error::Line coverage {rate:.1f}% is below the minimum of {minimum:.0f}%")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
