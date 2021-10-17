using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Microsoft.Data.SqlClient.Tools
{
	[Generator]
	public class SmiBitmapGenerator : ISourceGenerator
	{
		private static DiagnosticDescriptor invalid = new DiagnosticDescriptor("SQLSG001", "invalid csv file", "invalid csv file", "Source Generation", DiagnosticSeverity.Error, true);
		private static DiagnosticDescriptor invalidHeader = new DiagnosticDescriptor("SQLSG002", "invalid csv header", "invalid csv header", "Source Generation", DiagnosticSeverity.Error, true);
		private static DiagnosticDescriptor invalidRowLength = new DiagnosticDescriptor("SQLSG003", "invalid csv row", "invalid csv row", "Source Generation", DiagnosticSeverity.Error, true);

		public void Execute(GeneratorExecutionContext context)
		{
			//Debug();
			List<(AdditionalText, string)> mapFiles = GetMapFiles(context);
			if (mapFiles != null && mapFiles.Count > 0)
			{
				foreach (var (mapfile, typeName) in mapFiles)
				{
					var (map, error) = GenerateUncompressedMap(mapfile, typeName);
					if (error != null)
					{
						context.ReportDiagnostic(error);
					}
					else
					{
						WriteCompressedMap(map, context);
					}
				}
			}
		}

		private void WriteCompressedMap(UncompressedMap map, GeneratorExecutionContext context)
		{
			// note that this transformation switches rows and columns so that in the generated source code RowCount uses the column count value
			int rowCount = map.RowNames.Count;
			int columnCount = map.ColumnNames.Count;

			int bytesPerRow = Div32Rem(rowCount, out int additionalBits);
			if (additionalBits > 0)
			{
				bytesPerRow += 1;
			}
			byte[] bytes = new byte[bytesPerRow * columnCount];
			foreach (var (row, column) in map.Values)
			{
				var (byteIndex, bitIndex) = GetBitIndex(row, rowCount, column, columnCount);
				bytes[byteIndex] |= (byte)(1 << bitIndex);
			}

			StringBuilder buffer = new StringBuilder(1024);
			for (int index = 0; index < bytes.Length; index++)
			{
				if (index > 0)
				{
					buffer.Append(", ");
				}
				if (index % bytesPerRow == 0)
				{
					buffer.AppendLine();
					buffer.Append('\t', 4);
				}
				buffer.AppendFormat("0x{0:X2}", bytes[index]);
			}

			string rowTypeName = map.RowTypeName;
			string columnTypeName = map.ColumnTypeName;
			SortedSet<string> usings = new SortedSet<string>();
			usings.Add("System");
			usings.Add(GetNamespace(rowTypeName));
			usings.Add(GetNamespace(columnTypeName));

			StringBuilder usingsBuffer = new StringBuilder(128);
			foreach (var name in usings)
			{
				usingsBuffer.Append("using ");
				usingsBuffer.Append(name);
				usingsBuffer.AppendLine(";");
			}

			string code = $@"
{usingsBuffer}
namespace {GetNamespace(map.TypeName)}
{{
    internal sealed class {GetClassName(map.TypeName)}
    {{
        private const int RowCount = {map.ColumnNames.Count:D};

        private static ReadOnlySpan<byte> _map => new ReadOnlySpan<byte>(new byte[]
            {{{buffer}
            }}
        );

        internal static bool Lookup({GetClassName(rowTypeName)} row, {GetClassName(columnTypeName)} column)
        {{
            int offset = (int)row + ((int)column * RowCount);
            int byteIndex = (int)((uint)offset / 8);
            int bitIndex = offset & (8 - 1);
            return (_map[byteIndex] & (1 << bitIndex)) != 0;
        }}
    }}
}}";

			context.AddSource(GetClassName(map.TypeName), SourceText.From(code,Encoding.UTF8));
		}

		private static (UncompressedMap, Diagnostic) GenerateUncompressedMap(AdditionalText additionalText, string typeName)
		{
			UncompressedMap retval = new UncompressedMap();
			retval.TypeName = typeName;
			Diagnostic error = null;

			using (StringReader reader = new StringReader(additionalText.GetText().ToString()))
			{
				string line = reader.ReadLine();

				// get column headers
				if (!string.IsNullOrEmpty(line))
				{
					string[] parts = line.Split(new char[] { ',' }, StringSplitOptions.None);
					if (parts != null && parts.Length > 0)
					{
						string[] types = parts[0].Split(new char[] { '/', '\\' });
						if (types != null && types.Length == 2)
						{
							retval.RowTypeName = types[0];
							retval.ColumnTypeName = types[1];
						}

						foreach (string columnName in parts.AsSpan(1))
						{
							retval.ColumnNames.Add(columnName);
						}
					}
					else
					{
						error = Diagnostic.Create(invalidHeader,
							Location.Create(
								additionalText.Path,
								new TextSpan(0, line.Length),
								new LinePositionSpan(
									new LinePosition(0, 0),
									new LinePosition(0, line.Length - 1)
								)
							)
						);
					}
				}

				if (
					error == null &&
					!string.IsNullOrEmpty(retval.ColumnTypeName) &&
					!string.IsNullOrEmpty(retval.RowTypeName) &&
					retval.ColumnNames.Count > 0
				)
				{
					int row = 0;
					while ((line = reader.ReadLine()) != null)
					{
						string[] parts = line.Split(',');
						if (parts != null && parts.Length == retval.ColumnNames.Count + 1)
						{
							retval.RowNames.Add(parts[0]);
							for (int column = 1; column < parts.Length; column++)
							{
								string cell = parts[column];
								if (!string.IsNullOrEmpty(cell))
								{
									retval.Values.Add((row, column - 1));
								}
							}
						}
						else
						{
							error = Diagnostic.Create(
								invalidRowLength,
								Location.Create(
									additionalText.Path,
									new TextSpan(0, line.Length),
									new LinePositionSpan(
										new LinePosition(0, 0),
										new LinePosition(0, line.Length - 1)
									)
								)
							);
						}
						row += 1;
					}
				}
			}
			return (retval, error);
		}

		private class UncompressedMap
		{
			public string TypeName;
			public string ColumnTypeName;
			public string RowTypeName;
			public List<string> ColumnNames = new List<string>();
			public List<string> RowNames = new List<string>();
			public HashSet<(int Row, int Column)> Values = new HashSet<(int, int)>();
		}


		public void Initialize(GeneratorInitializationContext context)
		{

		}

		private List<(AdditionalText, string)> GetMapFiles(GeneratorExecutionContext context)
		{
			List<(AdditionalText, string)> list = null;
			if (context.AdditionalFiles != null)
			{
				foreach (var file in context.AdditionalFiles)
				{
					var options = context.AnalyzerConfigOptions.GetOptions(file);
					if (
						options.TryGetValue("build_metadata.AdditionalFiles.FileType", out string type) &&
						string.Equals(type, "Map", StringComparison.InvariantCultureIgnoreCase) &&
						options.TryGetValue("build_metadata.AdditionalFiles.FileFormat", out string format) &&
						string.Equals(format, "Csv", StringComparison.InvariantCultureIgnoreCase) &&
						options.TryGetValue("build_metadata.AdditionalFiles.FullyQualifiedOutputTypeName", out string typeName) &&
						!string.IsNullOrEmpty(typeName)
					)
					{
						if (list == null)
						{
							list = new List<(AdditionalText, string)>();
						}
						list.Add((file, typeName));
					}

				}
			}
			return list;
		}

		private static void Debug()
		{
			if (!Debugger.IsAttached)
			{
				Debugger.Launch();
			}
		}

		private static (int ByteIndex, int BitIndex) GetBitIndex(int row, int rowCount, int column, int columnCount)
		{
			if (row >= rowCount)
			{
				throw new ArgumentOutOfRangeException(nameof(row));
			}
			if (column >= columnCount)
			{
				throw new ArgumentOutOfRangeException(nameof(column));
			}
			int byteIndex = Div32Rem(row + (column * rowCount), out int bitIndex);
			return (byteIndex, bitIndex);
		}

		private static int Div32Rem(int number, out int remainder)
		{
			uint quotient = (uint)number / 8;
			remainder = number & (8 - 1);    // equivalent to number % 8, since 8 is a power of 2
			return (int)quotient;
		}

		private static string GetNamespace(string fullyQualifiedName)
		{
			string retval = fullyQualifiedName;
			int index = fullyQualifiedName.LastIndexOf('.');
			if (index > 0)
			{
				retval = fullyQualifiedName.Substring(0, index);
			}
			return retval;
		}
		private static string GetClassName(string fullyQualifiedName)
		{
			string retval = fullyQualifiedName;
			int index = fullyQualifiedName.LastIndexOf('.');
			if (index > 0 && index < (fullyQualifiedName.Length - 1))
			{
				retval = fullyQualifiedName.Substring(index + 1);
			}
			return retval;
		}
	}
}