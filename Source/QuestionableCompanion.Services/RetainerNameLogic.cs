using System;
using System.Collections.Generic;
using System.Linq;
using QuestionableCompanion.Models;

namespace QuestionableCompanion.Services;

public static class RetainerNameLogic
{
	public const int GameNameLengthLimit = 20;

	internal const int GeneratedLetterInsertionCount = 3;

	internal const int RawBaseLengthLimit = 17;

	internal const int OneWordSourceMinimumLength = 8;

	internal const int OneWordSourceMaximumLength = 19;

	internal const int OneWordMinimumLength = 9;

	public static IReadOnlyList<string> GenerateUniqueBatch(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, IEnumerable<string> unavailableNames, int count, Random random, int maxAttempts)
	{
		if (count < 0)
		{
			throw new ArgumentOutOfRangeException("count");
		}
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException("maxAttempts");
		}
		HashSet<string> hashSet = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		List<string> list = new List<string>(count);
		string name;
		while (list.Count < count && TryGenerateUniqueBase(firstNames, lastNames, hashSet, random, maxAttempts, out name))
		{
			hashSet.Add(name);
			list.Add(name);
		}
		return list;
	}

	public static bool TryCombine(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, Random random, out string name)
	{
		name = string.Empty;
		if (firstNames.Count == 0 || lastNames.Count == 0)
		{
			return false;
		}
		string value = firstNames[random.Next(firstNames.Count)].Trim();
		string value2 = lastNames[random.Next(lastNames.Count)].Trim();
		char value3 = ((random.Next(2) == 0) ? '-' : '\'');
		string text = $"{value}{value3}{value2}";
		if (!IsValidGeneratedName(text) || !HasExactlyOneSeparator(text))
		{
			return false;
		}
		name = text;
		return true;
	}

	internal static bool TryGenerateUniqueBase(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, IEnumerable<string> unavailableNames, Random random, int maxAttempts, out string name)
	{
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException("maxAttempts");
		}
		HashSet<string> unavailableNames2 = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		bool flag = random.Next(2) == 0;
		if (TryGenerateUniqueBaseForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, flag, out name))
		{
			return true;
		}
		return TryGenerateUniqueBaseForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, !flag, out name);
	}

	internal static bool TryGenerateInitialSessions(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, IEnumerable<string> unavailableNames, Random random, int maxAttempts, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException("maxAttempts");
		}
		HashSet<string> unavailableNames2 = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		bool flag = random.Next(2) == 0;
		if (TryGenerateInitialSessionsForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, flag, out original, out reversed))
		{
			return true;
		}
		return TryGenerateInitialSessionsForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, !flag, out original, out reversed);
	}

	internal static bool TryCreateInitialSessionsFromBase(string baseName, IEnumerable<string> unavailableNames, Random random, int maxAttempts, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		HashSet<string> hashSet = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		if (!TryBuildSession(baseName, hashSet, random, maxAttempts, out original))
		{
			reversed = EmptySession();
			return false;
		}
		hashSet.UnionWith(original.Candidates);
		return TryBuildSession(ReverseBase(baseName), hashSet, random, maxAttempts, out reversed);
	}

	internal static bool TryGenerateFreshSession(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, IEnumerable<string> unavailableNames, Random random, int maxAttempts, out RetainerNamingSession session)
	{
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException("maxAttempts");
		}
		HashSet<string> unavailableNames2 = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		bool flag = random.Next(2) == 0;
		if (TryGenerateSessionForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, flag, out session))
		{
			return true;
		}
		return TryGenerateSessionForFormat(firstNames, lastNames, unavailableNames2, random, maxAttempts, !flag, out session);
	}

	internal static bool TryBuildSession(string baseName, IEnumerable<string> unavailableNames, Random random, int maxAttempts, out RetainerNamingSession session)
	{
		session = EmptySession();
		if (maxAttempts < 0)
		{
			throw new ArgumentOutOfRangeException("maxAttempts");
		}
		if (!IsValidGeneratedName(baseName))
		{
			return false;
		}
		HashSet<string> hashSet = new HashSet<string>(unavailableNames, StringComparer.OrdinalIgnoreCase);
		if (!hashSet.Add(baseName))
		{
			return false;
		}
		List<string> list = new List<string>(3) { baseName };
		int[] array = new int[2] { 2, 4 };
		foreach (int changeCount in array)
		{
			bool flag = false;
			for (int j = 0; j < maxAttempts; j++)
			{
				if (TryMutateLetters(baseName, changeCount, random, out string mutation) && IsValidGeneratedName(mutation) && hashSet.Add(mutation))
				{
					list.Add(mutation);
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				return false;
			}
		}
		session = new RetainerNamingSession(baseName, list);
		return true;
	}

	internal static bool TryMutateLetters(string baseName, int changeCount, Random random, out string mutation)
	{
		mutation = string.Empty;
		if (changeCount < 0)
		{
			throw new ArgumentOutOfRangeException("changeCount");
		}
		List<int> list = (from item in baseName.Select((char character, int item) => (character: character, index: item))
			where char.IsLetter(item.character)
			select item.index).ToList();
		if (list.Count < changeCount)
		{
			return false;
		}
		char[] array = baseName.ToCharArray();
		for (int num = 0; num < changeCount; num++)
		{
			int index = random.Next(list.Count);
			int num2 = list[index];
			list.RemoveAt(index);
			char c = array[num2];
			char c2 = (char)(97 + random.Next(26));
			if (char.ToLowerInvariant(c) == c2)
			{
				c2 = (char)(97 + (c2 - 97 + 1) % 26);
			}
			array[num2] = (char.IsUpper(c) ? char.ToUpperInvariant(c2) : c2);
		}
		mutation = new string(array);
		return true;
	}

	internal static string ReverseBase(string baseName)
	{
		if (string.IsNullOrEmpty(baseName))
		{
			return string.Empty;
		}
		char[] array = baseName.ToLowerInvariant().Reverse().ToArray();
		array[0] = char.ToUpperInvariant(array[0]);
		return new string(array);
	}

	internal static bool TryAugmentGeneratedBase(string rawBase, Random random, out string augmented)
	{
		augmented = string.Empty;
		if (string.IsNullOrWhiteSpace(rawBase) || rawBase.Length > 17 || !IsValidGeneratedName(rawBase))
		{
			return false;
		}
		List<int> list = Enumerable.Range(1, rawBase.Length - 1).ToList();
		if (list.Count < 3)
		{
			return false;
		}
		List<(int, char)> list2 = new List<(int, char)>(3);
		for (int i = 0; i < 3; i++)
		{
			int index = random.Next(list.Count);
			int item = list[index];
			list.RemoveAt(index);
			list2.Add((item, (char)(97 + random.Next(26))));
		}
		augmented = rawBase;
		foreach (var item4 in list2.OrderByDescending<(int, char), int>(((int Position, char Letter) tuple) => tuple.Position))
		{
			int item2 = item4.Item1;
			char item3 = item4.Item2;
			augmented = augmented.Insert(item2, item3.ToString());
		}
		if (augmented.Length == rawBase.Length + 3 && augmented[0] == rawBase[0])
		{
			string obj = augmented;
			if (obj[obj.Length - 1] == rawBase[rawBase.Length - 1] && augmented.Count((char character) => (character == '\'' || character == '-') ? true : false) == rawBase.Count((char character) => (character == '\'' || character == '-') ? true : false))
			{
				return IsValidGeneratedName(augmented);
			}
		}
		return false;
	}

	internal static bool InvalidateGeneratedSampleCacheOnLoad(RetainerSetupConfiguration settings)
	{
		if (settings.SampleNames.Count == 0)
		{
			return false;
		}
		settings.SampleNames.Clear();
		return true;
	}

	internal static bool ShouldRegenerateHybridSampleCache(IReadOnlyCollection<string> samples)
	{
		if (samples.Count > 0)
		{
			return samples.All(HasExactlyOneSeparator);
		}
		return false;
	}

	internal static int CaseInsensitiveHammingDistance(string left, string right)
	{
		if (left.Length != right.Length)
		{
			return int.MaxValue;
		}
		return left.Where((char character, int index) => char.ToUpperInvariant(character) != char.ToUpperInvariant(right[index])).Count();
	}

	public static bool IsValidGeneratedName(string name)
	{
		if (string.IsNullOrWhiteSpace(name) || name.Length > 20)
		{
			return false;
		}
		if (name.Any((char character) => !char.IsLetter(character) && character != '-' && character != '\''))
		{
			return false;
		}
		switch (name.Count((char character) => (character == '\'' || character == '-') ? true : false))
		{
		case 0:
			return name.Length >= 9;
		case 1:
		{
			char c = name[0];
			int result;
			if (c != '-' && c != '\'')
			{
				c = name[name.Length - 1];
				result = ((c != '-' && c != '\'') ? 1 : 0);
			}
			else
			{
				result = 0;
			}
			return (byte)result != 0;
		}
		default:
			return false;
		}
	}

	private static bool TryGenerateUniqueBaseForFormat(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, ISet<string> unavailableNames, Random random, int maxAttempts, bool twoPart, out string name)
	{
		for (int i = 0; i < maxAttempts; i++)
		{
			if (TryGenerateForFormat(firstNames, lastNames, random, twoPart, out name) && !unavailableNames.Contains(name))
			{
				return true;
			}
		}
		name = string.Empty;
		return false;
	}

	private static bool TryGenerateInitialSessionsForFormat(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, ISet<string> unavailableNames, Random random, int maxAttempts, bool twoPart, out RetainerNamingSession original, out RetainerNamingSession reversed)
	{
		for (int i = 0; i < maxAttempts; i++)
		{
			if (TryGenerateForFormat(firstNames, lastNames, random, twoPart, out string name) && TryCreateInitialSessionsFromBase(name, unavailableNames, random, maxAttempts, out original, out reversed))
			{
				return true;
			}
		}
		original = EmptySession();
		reversed = EmptySession();
		return false;
	}

	private static bool TryGenerateSessionForFormat(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, ISet<string> unavailableNames, Random random, int maxAttempts, bool twoPart, out RetainerNamingSession session)
	{
		for (int i = 0; i < maxAttempts; i++)
		{
			if (TryGenerateForFormat(firstNames, lastNames, random, twoPart, out string name) && TryBuildSession(name, unavailableNames, random, maxAttempts, out session))
			{
				return true;
			}
		}
		session = EmptySession();
		return false;
	}

	private static bool TryGenerateForFormat(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, Random random, bool twoPart, out string name)
	{
		if (!(twoPart ? TryCombine(firstNames, lastNames, random, out string name2) : TryCreateOneWord(firstNames, lastNames, random, out name2)) || !TryAugmentGeneratedBase(name2, random, out name))
		{
			name = string.Empty;
			return false;
		}
		return true;
	}

	internal static bool TryCreateOneWord(IReadOnlyList<string> firstNames, IReadOnlyList<string> lastNames, Random random, out string name)
	{
		string[] array = (from value in firstNames.Concat(lastNames)
			select value.Trim()).Where(delegate(string value)
		{
			int length = value.Length;
			return length >= 8 && length <= 19 && value.All(char.IsLetter);
		}).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToArray();
		if (array.Length == 0)
		{
			name = string.Empty;
			return false;
		}
		string text = array[random.Next(array.Length)];
		string text2 = char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();
		int startIndex = random.Next(1, text2.Length);
		name = text2.Insert(startIndex, ((char)(97 + random.Next(26))).ToString());
		if (IsValidGeneratedName(name))
		{
			return !HasExactlyOneSeparator(name);
		}
		return false;
	}

	private static bool HasExactlyOneSeparator(string name)
	{
		return name.Count((char character) => (character == '\'' || character == '-') ? true : false) == 1;
	}

	private static RetainerNamingSession EmptySession()
	{
		return new RetainerNamingSession(string.Empty, Array.Empty<string>());
	}
}
