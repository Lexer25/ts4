using System;
using System.Collections.Generic;
using System.Data;

namespace TS4
{
	public static class Device
	{
		/// <summary>
		/// Строит список DEV, сопоставляя записи Firebird (id_dev+name) с данными из Access по Name.
		/// - accessDevices: таблица из DBAccess.GetDevice (ID, Name, ConnectionString, Driver)
		/// - firebirdDevices: список из DBFirebird.GetListForLoad (id_dev, name)
		/// Возвращает только те устройства, которые найдены по имени в Access.
		/// </summary>
		public static List<DEV> BuildFromAccessAndFirebird(DataTable accessDevices, List<(int idDev, string name)> firebirdDevices)
		{
			var result = new List<DEV>();
			if (accessDevices == null || firebirdDevices == null || firebirdDevices.Count == 0)
			{
				return result;
			}

			var nameToRow = new Dictionary<string, DataRow>(StringComparer.OrdinalIgnoreCase);
			foreach (DataRow row in accessDevices.Rows)
			{
				var name = row["Name"]?.ToString() ?? string.Empty;
				if (!string.IsNullOrWhiteSpace(name) && !nameToRow.ContainsKey(name))
				{
					nameToRow[name] = row;
				}
			}

			foreach (var fb in firebirdDevices)
			{
				if (string.IsNullOrWhiteSpace(fb.name)) continue;
				if (!nameToRow.TryGetValue(fb.name, out var row)) continue;

				var dev = new DEV(row)
				{
					id = fb.idDev
				};
				result.Add(dev);
			}

			return result;
		}
	}
}
