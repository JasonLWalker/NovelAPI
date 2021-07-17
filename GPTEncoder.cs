﻿using RestSharp;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace net.novelai.api {
	public class gpt_bpe {
		public struct GPTEncoder {
			public Dictionary<string, int> encoder;
			public Dictionary<int, string> decoder;
			public Dictionary<GPTPair, double> bpe_ranks;
			public Regex pattern;
			public Dictionary<byte, char> byteToRune;
			public Dictionary<char, byte> runeToByte;
			public Dictionary<string, string[]> cache;

			public ushort[] Encode(string text) {
				string[] words = SplitWords(text);
				List<ushort> encoded = new List<ushort>();
				for(int idx = 0; idx < words.Length; idx++) {
					string fragment = toUnicode(words[idx]);
					string[] token = toBPE(fragment);
					encoded.AddRange(encodeTokens(token));
				}
				return encoded.ToArray();
			}

			public string Decode(ushort[] encoded) {
				// First convert our `uint16` tokens into an 8-bit byte array.
				List<char> bs = new List<char>();
				for(int idx = 0; idx < encoded.Length; idx++) {
					if(decoder.ContainsKey(encoded[idx])) {
						string v = decoder[encoded[idx]];
						bs.AddRange(v.ToCharArray());
					}
				}
				for(int i=0; i < bs.Count; i++) {
					bs[i] = (char)(bs[i] > 255 ? bs[i] - 256 : bs[i]);
				}
				return string.Join("", bs);
			}

			public BGERank[] rankPairs(GPTPair[] pairs) {
				List<BGERank> rankedPairs = new List<BGERank>();
				for(int idx = 0; idx < pairs.Length; idx++) {
					double bpe;
					if(bpe_ranks.ContainsKey(pairs[idx])) {
						bpe = bpe_ranks[pairs[idx]];
					}
					else {
						bpe = double.PositiveInfinity;
					}
					rankedPairs.Add(new BGERank {
						rank = bpe,
						bigram = pairs[idx]
					});
				}
				rankedPairs.Sort((x,y) => x.rank.CompareTo(y.rank));
				return rankedPairs.ToArray();
			}

			public GPTPair minPair(GPTPair[] pairs) {
				BGERank[] rankedPairs = rankPairs(pairs);
				if(rankedPairs.Length > 0) {
					return rankedPairs[0].bigram;
				}
				return new GPTPair();
			}

			public string toUnicode(string text) {
				string result = "";
				foreach(char c in text) {
					byte b = (byte)c;
					result += byteToRune[b];
				}
				return result;
			}

			public ushort[] encodeTokens(string[] tokens) {
				List<ushort> encoded = new List<ushort>();
				for(int idx = 0; idx < tokens.Length; idx++) {
					if(encoder.ContainsKey(tokens[idx]))
						encoded.Add((ushort)encoder[tokens[idx]]);
				}
				return encoded.ToArray();
			}

			public string[] toBPE(string text) {
				if(cache.ContainsKey(text)) return cache[text];
				string[] word = Regex.Split(text, string.Empty);
				GPTPair[] pairs = getPairs(word);
				if(pairs.Length == 0) {
					return new string[] { text };
				}
				while(true) {
					GPTPair bigram = minPair(pairs);
					if(!bpe_ranks.ContainsKey(bigram))
						break;
					string first = bigram.left;
					string second = bigram.right;
					List<string> newWord = new List<string>();
					for(int i = 0; i < word.Length; ){
						int j = pos(word, first, i);
						if(j == -1) {
							for(int k = i; k < word.Length; k++)
								newWord.Add(word[k]);
							break;
						}
						for(int k = i; k < j; k++)
							newWord.Add(word[k]);
						i = j;
						if(word[i] == first && i < word.Length - 1 && word[i + 1] == second) {
							newWord.Add(first + second);
							i += 2;
						}
						else {
							newWord.Add(word[i]);
							i += 1;
						}
					}
					word = newWord.ToArray();
					if(word.Length == 1) {
						break;
					}
					else {
						pairs = getPairs(word);
					}
				}
				cache[text] = word;
				return word;
			}

			public string[] SplitWords(string text) {
				int[][] idxes = pattern.FindAllStringIndex(text, 0);
				List<string> words = new List<string>();
				for(int i=0; i < idxes.Length; i++) {
					words.Add(text.Substring(idxes[i][0], idxes[i][1]));
				}
				return words.ToArray();
			}
		}

		public struct GPTPair {
			public string left;
			public string right;
		}

		public struct BGERank {
			public double rank;
			public GPTPair bigram;
		}

		public static GPTPair[] getPairs(string[] word) {
			Dictionary<GPTPair, bool> pairsSet = new Dictionary<GPTPair, bool>();
			List<GPTPair> pairs = new List<GPTPair>();
			int begin = 0;
			string prev = word[0];
			for(int idx = begin; idx < word.Length ; idx++) {
				string present = word[idx];
				GPTPair pair = new GPTPair {
					left = prev,
					right = present
				};
				if(!pairsSet.ContainsKey(pair)) {
					pairs.Add(pair);
				}
				pairsSet[pair] = true;
				prev = present;
			}
			return pairs.ToArray();
		}

		public static int pos(string[] word, string seek, int i) {
			for(int j = i; j < word.Length; j++) {
				if(seek == word[j])
					return j;
			}
			return -1;
		}

		public static GPTEncoder NewEncoder() {
			string json = File.ReadAllText("./encoder.json");
			Dictionary<string, int> encoderTokens = SimpleJson.DeserializeObject<Dictionary<string, int>>(json);
			Dictionary<int, string> tokensEncoder = new Dictionary<int, string>();
			foreach(KeyValuePair<string, int> entry in encoderTokens) {
				tokensEncoder.Add(entry.Value, entry.Key);
			}
			Dictionary<GPTPair, double> bpeRanks = new Dictionary<GPTPair, double>();
			bool firstLine = true;
			ushort idx = 0;
			foreach(string line in File.ReadAllLines("./vocab.bpe")) {
				if(firstLine) {
					firstLine = false;
					continue;
				}
				string[] left_right = line.Split(' ');
				GPTPair p = new GPTPair { left = left_right[0], right = left_right[1] };
				bpeRanks[p] = idx;
				idx++;
			}
			Regex pat = new Regex("'s|'t|'re|'ve|'m|'ll|'d| ?\\p{L}+| ?\\p{N}+| ?[^\\s\\p{L}\\p{N}]+|\\s+(\\S){0}|\\s+");
			List<byte> bs = new List<byte>();
			Dictionary<byte, char> bytesUnicode = new Dictionary<byte, char>();
			Dictionary<char, byte> unicodeBytes = new Dictionary<char, byte>();
			char gc = 'Ġ';
			ushort gb = (ushort)gc;
			for(byte b = (byte)'!'; b < (byte)'~' + 1; b++) {
				bs.Add(b);
				bytesUnicode[b] = (char)b;
				unicodeBytes[(char)b] = b;
			}
			for(byte b = (byte)'¡'; b < (byte)'¬' + 1; b++) {
				bs.Add(b);
				bytesUnicode[b] = (char)b;
				unicodeBytes[(char)b] = b;
			}
			for(ushort b = '®'; b < 'ÿ' + 1; b++) {
				bs.Add((byte)b);
				bytesUnicode[(byte)b] = (char)b;
				unicodeBytes[(char)b] = (byte)b;
			}
			int uct = 0;
			for(ushort b = 0; b < 256; b++) {
				byte bb = (byte)b;
				if(!bytesUnicode.ContainsKey(bb)) {
					bytesUnicode[(byte)b] = (char)(256 + uct);
					unicodeBytes[(char)(256 + uct)] = (byte)b;
					uct += 1;
				}
			}
			return new GPTEncoder {
				encoder = encoderTokens,
				decoder = tokensEncoder,
				bpe_ranks = bpeRanks,
				pattern = pat,
				byteToRune = bytesUnicode,
				runeToByte = unicodeBytes,
				cache = new Dictionary<string, string[]>()
			};
		}
	}
}
