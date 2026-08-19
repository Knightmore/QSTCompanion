using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public sealed class RetainerNameGenerator
{
	public const int GameNameLengthLimit = 20;

	private const int DefaultBatchAttemptLimit = 500;

	private readonly IDataManager dataManager;

	private readonly Random random;

	public RetainerNameGenerator(IDataManager dataManager, Random? random = null)
	{
		this.dataManager = dataManager;
		this.random = random ?? new Random();
	}

	public IReadOnlyList<string> GenerateSamples(RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan, IEnumerable<string> unavailableNames, int count = 10)
	{
		var (firstNames, lastNames) = ReadNameParts(race, gender, clan);
		return GenerateUniqueBatch(firstNames, lastNames, unavailableNames, count, random, 500);
	}

	public bool TryGenerateName(RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan, ISet<string> unavailableNames, int maxAttempts, out string name)
	{
		var (firstNames, lastNames) = ReadNameParts(race, gender, clan);
		if (!RetainerNameLogic.TryGenerateUniqueBase(firstNames, lastNames, unavailableNames, random, maxAttempts, out name))
		{
			return false;
		}
		unavailableNames.Add(name);
		return true;
	}

	internal bool TryCreateInitialSessionsFromBase(string baseName, IEnumerable<string> unavailableNames, int maxAttempts, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		return RetainerNameLogic.TryCreateInitialSessionsFromBase(baseName, unavailableNames, random, maxAttempts, out original, out reversed);
	}

	internal bool TryCreateSessionFromBase(string baseName, IEnumerable<string> unavailableNames, int maxAttempts, out RetainerNamingSession session)
	{
		return RetainerNameLogic.TryBuildSession(baseName, unavailableNames, random, maxAttempts, out session);
	}

	internal bool TryGenerateInitialSessions(RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan, IEnumerable<string> unavailableNames, int maxAttempts, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		var (firstNames, lastNames) = ReadNameParts(race, gender, clan);
		return RetainerNameLogic.TryGenerateInitialSessions(firstNames, lastNames, unavailableNames, random, maxAttempts, out original, out reversed);
	}

	internal bool TryGenerateFreshSession(RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan, IEnumerable<string> unavailableNames, int maxAttempts, out RetainerNamingSession session)
	{
		var (firstNames, lastNames) = ReadNameParts(race, gender, clan);
		return RetainerNameLogic.TryGenerateFreshSession(firstNames, lastNames, unavailableNames, random, maxAttempts, out session);
	}

	public static IReadOnlyList<string> GenerateUniqueBatch(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, IEnumerable<string> unavailableNames, int count, Random random, int maxAttempts)
	{
		return RetainerNameLogic.GenerateUniqueBatch(firstNames, lastNames, unavailableNames, count, random, maxAttempts);
	}

	public static bool IsValidGeneratedName(string name)
	{
		return RetainerNameLogic.IsValidGeneratedName(name);
	}

	private (IReadOnlyList<string> FirstNames, IReadOnlyList<string> LastNames) ReadNameParts(RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan)
	{
		ExcelSheet<CharaMakeName> excelSheet = dataManager.GetExcelSheet<CharaMakeName>();
		PropertyInfo[] source = (from property in typeof(CharaMakeName).GetProperties(BindingFlags.Instance | BindingFlags.Public)
			where property.PropertyType == typeof(ReadOnlySeString) && !property.Name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)
			select property).ToArray();
		PropertyInfo[] array = source.Where(IsFirstNamePart).ToArray();
		PropertyInfo[] array2 = source.Where(IsLastNamePart).ToArray();
		PropertyInfo[] array3 = ApplyAppearanceFilter(array, race, gender, clan).ToArray();
		PropertyInfo[] array4 = ApplyAppearanceFilter(array2, race, RetainerGender.Random, clan).ToArray();
		if (array3.Length == 0)
		{
			array3 = array;
		}
		if (array4.Length == 0)
		{
			array4 = array2;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (CharaMakeName item in excelSheet)
		{
			ReadProperties(item, array3, hashSet);
			ReadProperties(item, array4, hashSet2);
		}
		hashSet.RemoveWhere((string value) => value.Equals("Ilcum", StringComparison.OrdinalIgnoreCase));
		return (FirstNames: hashSet.ToArray(), LastNames: hashSet2.ToArray());
	}

	private static IEnumerable<PropertyInfo> ApplyAppearanceFilter(IEnumerable<PropertyInfo> properties, RetainerAppearanceRace race, RetainerGender gender, RetainerClan clan)
	{
		PropertyInfo[] array = properties.ToArray();
		if (race != RetainerAppearanceRace.Random)
		{
			string raceToken = race.ToString();
			PropertyInfo[] array2 = array.Where((PropertyInfo property) => property.Name.Contains(raceToken, StringComparison.OrdinalIgnoreCase)).ToArray();
			if (array2.Length != 0)
			{
				array = array2;
			}
			if (clan != RetainerClan.Random)
			{
				(string, string) clanTokens = GetClanTokens(race);
				string firstClan = clanTokens.Item1;
				string secondClan = clanTokens.Item2;
				string desiredClan = ((clan == RetainerClan.First) ? firstClan : secondClan);
				PropertyInfo[] array3 = array.Where((PropertyInfo property) => property.Name.Contains(desiredClan, StringComparison.OrdinalIgnoreCase) || (!property.Name.Contains(firstClan, StringComparison.OrdinalIgnoreCase) && !property.Name.Contains(secondClan, StringComparison.OrdinalIgnoreCase))).ToArray();
				if (array3.Length != 0)
				{
					array = array3;
				}
			}
		}
		if (gender != RetainerGender.Random)
		{
			string token = ((gender == RetainerGender.Male) ? "Male" : "Female");
			PropertyInfo[] array4 = array.Where((PropertyInfo property) => property.Name.Contains(token, StringComparison.OrdinalIgnoreCase) || (!property.Name.Contains("Male", StringComparison.OrdinalIgnoreCase) && !property.Name.Contains("Female", StringComparison.OrdinalIgnoreCase))).ToArray();
			if (array4.Length != 0)
			{
				array = array4;
			}
		}
		return array;
	}

	private static bool IsFirstNamePart(PropertyInfo property)
	{
		if (!IsLastNamePart(property))
		{
			if (!property.Name.Contains("Male", StringComparison.OrdinalIgnoreCase) && !property.Name.Contains("Female", StringComparison.OrdinalIgnoreCase))
			{
				return property.Name.Contains("FirstName", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static bool IsLastNamePart(PropertyInfo property)
	{
		if (!property.Name.Contains("LastName", StringComparison.OrdinalIgnoreCase))
		{
			return property.Name.Contains("EndOfNames", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static (string First, string Second) GetClanTokens(RetainerAppearanceRace race)
	{
		return race switch
		{
			RetainerAppearanceRace.Hyur => (First: "Midlander", Second: "Highlander"), 
			RetainerAppearanceRace.Elezen => (First: "Wildwood", Second: "Duskwight"), 
			RetainerAppearanceRace.Lalafell => (First: "Plainsfolk", Second: "Dunesfolk"), 
			RetainerAppearanceRace.Miqote => (First: "Sun", Second: "Moon"), 
			RetainerAppearanceRace.Roegadyn => (First: "SeaWolf", Second: "Hellsguard"), 
			RetainerAppearanceRace.AuRa => (First: "Raen", Second: "Xaela"), 
			RetainerAppearanceRace.Hrothgar => (First: "Hellions", Second: "Lost"), 
			RetainerAppearanceRace.Viera => (First: "Rava", Second: "Veena"), 
			_ => (First: string.Empty, Second: string.Empty), 
		};
	}

	private static void ReadProperties(CharaMakeName row, IEnumerable<PropertyInfo> properties, ISet<string> output)
	{
		foreach (PropertyInfo property in properties)
		{
			if (property.GetValue(row) is ReadOnlySeString readOnlySeString)
			{
				string text = readOnlySeString.ExtractText().Trim();
				if (!string.IsNullOrWhiteSpace(text))
				{
					output.Add(text);
				}
			}
		}
	}
}
