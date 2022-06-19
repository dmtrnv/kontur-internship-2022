using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Morphology.Common;
using Morphology.Common.Extensions;
using Morphology.Data;

namespace Morphology
{
    public class SentenceMorpher
    {
        private WordStorage _data = new();

        /// <summary>
        /// Создает <see cref="SentenceMorpher"/> из переданного набора строк словаря.
        /// </summary>
        /// <param name="dictionaryLines">Строки исходного словаря OpenCorpora в формате plain-text.</param>
        public static SentenceMorpher Create(IEnumerable<string> dictionaryLines)
        {
            var sm = new SentenceMorpher
            {
                _data = new WordStorage(dictionaryLines)
            };
            
            return sm;
        }

        /// <summary>
        /// Выполняет склонение предложения согласно указанному формату
        /// </summary>
        /// <param name="sentence">Входное предложение</param>
        public virtual string Morph(string sentence)
        {
            var splittedSentence = sentence.Split(new [] { ' ', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            
            if (splittedSentence.Length == 1)
            {
                return splittedSentence[0].Split(new [] { '{', '}' })[0];
            }

            var sentenceBuilder = new SentenceBuilder(_data);
            
            var attributeGroupInProcess = false;
            var word = string.Empty;
            var attributes = new HashSet<string>();
            
            foreach (var line in splittedSentence)
            {
                if (line.EndsWith('}'))
                {
                    attributeGroupInProcess = false;
                    
                    attributes.Add(line.Substring(0, line.Length - 1).ToUpperInvariant());
                    sentenceBuilder.Append(word, attributes);
                    
                    continue;
                }
                
                if (line.Contains('{'))
                {
                    attributeGroupInProcess = true;
                    
                    var splitLine = line.Split(new [] { '{' } );
                    word = splitLine[0];
                    
                    attributes = new HashSet<string> { splitLine[1].ToUpperInvariant() };

                    continue;
                }
                
                if (!attributeGroupInProcess)
                {
                    sentenceBuilder.Append(line);
                    continue;
                }
                
                attributes.Add(line.ToUpperInvariant());
            }

            return sentenceBuilder.ToString();
        }
    }
}

namespace Morphology.Data
{
    internal struct WordForm
    {
        public string Value;
        public HashSet<string> Attributes;
    }
    
    internal class WordStorage
    {
        private readonly Dictionary<string, List<WordForm>> _data = new();

        private static readonly char[] Separator = { '\t', ' ', ',', '{', '}' };

        public WordStorage() { }
        
        public WordStorage(IEnumerable<string> data)
        {
            AddRange(data);
        }
        
        public void AddRange(IEnumerable<string> dictionaryLines)
        {
            var lineIsWordNormalForm = false;
            var wordNormalForm = string.Empty;
            
            foreach (var line in dictionaryLines)
            {
                var splittedLine = line.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
                
                if (splittedLine.Length == 0 || int.TryParse(splittedLine[0], out _))
                {
                    lineIsWordNormalForm = true;
                    continue;
                }

                if (lineIsWordNormalForm)
                {
                    wordNormalForm = splittedLine[0].ToUpperInvariant();
                    
                    if (!_data.ContainsKey(wordNormalForm))
                    {
                        _data[wordNormalForm] = new List<WordForm>();
                    }
                    
                    lineIsWordNormalForm = false;
                }

                _data[wordNormalForm].Add(new WordForm
                {
                    Value = splittedLine[0],
                    Attributes = splittedLine
                        .Skip(1)
                        .Select(s => s.ToUpperInvariant())
                        .ToHashSet()
                });
            }
        }
        
        /// <param name="word">Слово в нормальной форме</param>
        public bool ContainsWord(string word)
        {
            return _data.ContainsKey(word);
        }
        
        /// <param name="word">Слово в нормальной форме</param>
        public List<WordForm> this[string word] => _data[word];
    }
}

namespace Morphology.Common
{
    internal class SentenceBuilder
    {
        private readonly WordStorage _data;
        private readonly StringBuilder _stringBuilder = new();

        public SentenceBuilder(WordStorage data)
        {
            _data = data;
        }
        
        /// <summary>
        /// Добавляет к предложению слово.
        /// Если в словаре не найдено ни одного совпадения или множество атрибутов отсутствует, то добавляет исходное слово.
        /// Иначе добавляет слово в указанной атрибутами форме.
        /// Слова соединены пробелами.
        /// </summary>
        /// <param name="word">Слово в нормальной фоорме</param>
        /// <param name="attributes">Множество атрибутов для поиска соответствующей формы слова в словаре</param>
        public void Append(string word, HashSet<string> attributes = default!)
        {
            if (attributes == default!)
            {
                _stringBuilder.AppendWithSpaceAfter(word);
                return;
            }
            
            var upperWord = word.ToUpperInvariant();
            
            if (_data.ContainsWord(upperWord))
            {
                var matched = false;
                    
                foreach (var wordForm in _data[upperWord])
                {
                    if (attributes.IsSubsetOf(wordForm.Attributes))
                    {
                        _stringBuilder.AppendWithSpaceAfter(wordForm.Value);

                        matched = true;
                        break;
                    }
                }
                    
                if (!matched)
                {
                    _stringBuilder.AppendWithSpaceAfter(word);
                }
            }
        }

        public override string ToString()
        {
            return _stringBuilder.ToString().TrimEnd();
        }
    }
}

namespace Morphology.Common.Extensions
{
    public static class StringBuilderExtensions
    {
        public static void AppendWithSpaceAfter(this StringBuilder sb, string str)
        {
            sb.Append(str);
            sb.Append(' ');
        }
    }
}