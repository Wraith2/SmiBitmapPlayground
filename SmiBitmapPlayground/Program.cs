using Microsoft.Data.SqlClient.Server;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;

namespace SmiBitmapPlayground
{
	class Program
	{
		static void Main(string[] args)
		{
			ExtendedClrTypeCode[] codes = ((ExtendedClrTypeCode[])Enum.GetValues(typeof(ExtendedClrTypeCode)))
				.Where(value=>value!=ExtendedClrTypeCode.Invalid)
				.ToArray();
			
			SqlDbType[] types = (SqlDbType[])Enum.GetValues(typeof(SqlDbType));

			foreach (var code in codes)
			{
				foreach (var type in types)
				{
					ValueUtilsSmi_Getters.Lookup(code, type);
				}
			}
		}
	}
}
