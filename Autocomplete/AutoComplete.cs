using System;
using System.Collections.Generic;
using System.Linq;
using AutoComplete.Common;

namespace AutoComplete
{
    public struct FullName
    {
        public string Name;
        public string Surname;
        public string Patronymic;
    }

    public class AutoCompleter
    {
        private readonly Dictionary<char, Dictionary<char, List<string>>> _data = new();

        /// <summary>
        /// Добавить ФИО в словарь для поиска
        /// </summary>
        /// <param name="fullNames">Список <see cref="FullName"/></param>
        public void AddToSearch(List<FullName> fullNames)
        {
            foreach (var fullName in fullNames)
            {
                var parsedFullName = FullNameParser.Parse(fullName);
                if (parsedFullName.Length == 0)
                {
                    continue;
                }
                
                var fullNameFirstChar = char.ToUpperInvariant(parsedFullName[0]);
                var fullNameSecondChar = char.ToUpperInvariant(parsedFullName[1]);
                
                if (!_data.ContainsKey(fullNameFirstChar))
                {
                    _data[fullNameFirstChar] = new Dictionary<char, List<string>>();
                }
                
                if (!_data[fullNameFirstChar].ContainsKey(fullNameSecondChar))
                {
                    _data[fullNameFirstChar][fullNameSecondChar] = new List<string>();
                }
                
                _data[fullNameFirstChar][fullNameSecondChar].Add(parsedFullName);
            }
        }

        /// <summary>
        /// Найти ФИО, начинающиеся на префикс
        /// </summary>
        /// <param name="prefix"></param>
        /// <returns>Список ФИО</returns>
        public List<string> Search(string prefix)
        {
            CheckPrefix(prefix);
            
            var prefixFirstChar = char.ToUpperInvariant(prefix[0]);
            if (!_data.ContainsKey(prefixFirstChar))
            {
                return new List<string>();
            }
            
            if (prefix.Length == 1)
            {
                return GetFullNamesStartsWithLetter(prefixFirstChar);
            }

            var prefixSecondChar = char.ToUpperInvariant(prefix[1]);
            
            return !_data[prefixFirstChar].ContainsKey(prefixSecondChar) 
                ? GetFullNamesStartsWithLetter(prefixFirstChar) 
                : GetFullNamesStartsWithPrefix(prefix);
        }
        
        private void CheckPrefix(string prefix)
        {
            if (prefix is null)
            {
                throw new ArgumentNullException($"{nameof(prefix)} = null.");        
            }
            
            if (prefix.Length is 0 or > 100)
            {
                throw new ArgumentOutOfRangeException($"Допустимый диапазон длины {nameof(prefix)} - [1; 100].");
            }
            
            if (string.IsNullOrWhiteSpace(prefix))
            {
                throw new ArgumentException($"{nameof(prefix)} состоит из пробелов.");
            }
        }
        
        private List<string> GetFullNamesStartsWithLetter(char letter)
        {
            var matchedFullNames = new List<string>();

            return _data[letter].Aggregate(matchedFullNames, 
                (current, pair) => current
                    .Concat(pair.Value)
                    .ToList());
        }
        
        private List<string> GetFullNamesStartsWithPrefix(string prefix)
        {
            var prefixFirstChar = char.ToUpperInvariant(prefix[0]);
            var prefixSecondChar = char.ToUpperInvariant(prefix[1]);
            
            return _data[prefixFirstChar][prefixSecondChar]
                .Where(fullName => prefix.Length <= fullName.Length)
                .Where(fullName => fullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }
}

namespace AutoComplete.Common
{
    internal static class FullNameParser
    {
        public static string Parse(FullName fullName)
        {
            return string.Join(' ', new [] { fullName.Surname, fullName.Name, fullName.Patronymic }
                .Where(s => !string.IsNullOrEmpty(s) && !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim()));
        }
    }
}