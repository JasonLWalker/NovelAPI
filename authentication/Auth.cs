﻿using Konscious.Security.Cryptography;
using net.novelai.api;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static net.novelai.api.Structs;

namespace net.novelai.authentication {
	public static class Auth {
		public static string GetAccessToken(string access_key) {
			//https://api.novelai.net/user/login
			RestClient client = new RestClient("https://api.novelai.net/");
			RestRequest request = new RestRequest("user/login");
			Dictionary<string, string> parms = new Dictionary<string, string>();
			parms.Add("key", access_key);
			string json = SimpleJson.SerializeObject(parms);
			request.AddJsonBody(json, "application/json");
			request.AddHeader("Content-Type", "application/json");
			IRestResponse response = client.Post(request);
			if(response.IsSuccessful) {
				Console.WriteLine("Loging successful");
				Dictionary<string, string> resp_decoded = SimpleJson.DeserializeObject<Dictionary<string, string>>(response.Content);
				return resp_decoded["accessToken"];
			}
			else {
				Console.WriteLine("Loging failed :(");
				Console.WriteLine(response.StatusCode);
				Console.WriteLine(response.ErrorException);
				Console.WriteLine(response.ErrorMessage);
				return string.Empty;
			}
		}

		public static string ByteArrayToString(byte[] ba) {
			return BitConverter.ToString(ba).Replace("-", "");
		}

		public static byte[] NaiHashArgon(int size, string plaintext, string secret, string domain) {
			HMACBlake2B encoder = new HMACBlake2B(null, 16*8);//param is bits
			var salt = encoder.ComputeHash(Encoding.UTF8.GetBytes(secret + domain));
			var argon2 = new Argon2id(Encoding.UTF8.GetBytes(plaintext)) {
				Salt = salt,
				DegreeOfParallelism = 1,
				MemorySize = 2000000/1024,
				Iterations = 2
			};
			return argon2.GetBytes(size);
		}

		public static NaiKeys NaiGenerateKeys(string email, string password) {
			string access_string = "";
			byte[] encryption_key = new byte[0];
			string pw_email_secret = password.Substring(0, 6) + email;
			encryption_key = NaiHashArgon(128,
				password,
				pw_email_secret,
				"novelai_data_encryption_key");
			byte[] access_key = NaiHashArgon(64,
				password,
				pw_email_secret,
				"novelai_data_access_key");

			access_string = Convert.ToBase64String(access_key).Substring(0, 64);
			access_string = access_string.Replace("/", "_");
			access_string = access_string.Replace("+", "-");
			return new NaiKeys {
				EncryptionKey = encryption_key,
				AccessKey = access_string,
			};
		}

		public static string[] GenerateUsernames(string email) {
			string[] usernames;
			string titleCase = email.ToUpper()[0] + email.ToLower().Substring(1);
			if(email.ToLower() != email) {
				usernames = new string[] { email, email.ToLower(), titleCase };
			}
			else {
				usernames = new string[] { email, titleCase };
			}
			return usernames;
		}

		public static NaiKeys AuthKeys(string email, string password) {
			string[] usernames = GenerateUsernames(email);
			NaiKeys keys = new NaiKeys();
			foreach(string username in usernames) {
				keys = NaiGenerateKeys(username, password);
				keys.AccessToken = GetAccessToken(keys.AccessKey);
				if(!string.IsNullOrEmpty(keys.AccessToken)) {
					break;
				}
			}
			if(string.IsNullOrEmpty(keys.AccessToken)) {
				Console.WriteLine("Failed to authenticate with NovelAI!");
			}
			return keys;
		}

		public static NaiKeys AuthEnv() {
			if(!Directory.Exists("./config")) {
				Directory.CreateDirectory("./config");
			}
			if(File.Exists("./config/auth.json")) {
				string json = File.ReadAllText("./config/auth.json");
				Dictionary<string, string> authCfg = SimpleJson.DeserializeObject<Dictionary<string, string>>(json);
				/*if(authCfg.ContainsKey("AccessKey")) { //Fallback override
					return new NaiKeys {
						AccessToken = GetAccessToken(authCfg["AccessKey"]),
					};
				}*/

				NaiKeys auth = AuthKeys(authCfg["Username"], authCfg["Password"]);
				if(auth.AccessToken.Length == 0) {
					Console.WriteLine("auth: failed to obtain AccessToken!");
				}
				else {
					AuthConfig upAuth = new AuthConfig {
						Username = authCfg["Username"],
						Password = authCfg["Password"],
					};
					File.WriteAllText("./config/auth.json", SimpleJson.SerializeObject(upAuth));
				}
				return auth;
			}
			else {
				AuthConfig newAuth = new AuthConfig {
					Username = "<empty>",
					Password = "<empty>",
				};
				File.WriteAllText("./config/auth.json", SimpleJson.SerializeObject(newAuth));
				return AuthKeys(newAuth.Username, newAuth.Password);
			}
		}

		public static Dictionary<string, byte[]> GetKeystore(NaiKeys keys) {
			Dictionary<string, byte[]> store = new Dictionary<string, byte[]>();
			//https://api.novelai.net/user/keystore
			RestClient client = new RestClient("https://api.novelai.net/");
			RestRequest request = new RestRequest("user/keystore");
			//Dictionary<string, string> parms = new Dictionary<string, string>();
			//parms.Add("key", keys.AccessKey);
			//string json = SimpleJson.SerializeObject(parms);
			//request.AddJsonBody(json, "application/json");
			request.AddHeader("Content-Type", "application/json");
			request.AddHeader("Authorization", "Bearer " + keys.AccessToken);
			IRestResponse response = client.Get(request);
			if(response.IsSuccessful) {
				Dictionary<string, object> raw = SimpleJson.DeserializeObject<Dictionary<string, object>>(response.Content);
				if(raw.ContainsKey("keystore")) {
					byte[] bytes = Convert.FromBase64String((string)raw["keystore"]);
					string str = Encoding.Default.GetString(bytes);
					Dictionary<string, object> raw2 = SimpleJson.DeserializeObject<Dictionary<string, object>>(str);
					if(raw2.ContainsKey("nonce") && raw2.ContainsKey("sdata")) {
						object[] nonceo = (object[])raw2["nonce"];
						object[] sdatao = (object[])raw2["sdata"];

						//List<object> list = new List<object>(nonceo);
						byte[] nonce = new byte[nonceo.Length]; //list.ConvertAll(x => (byte)x).ToArray();
						//list = new List<object>(sdatao);
						byte[] sdata = new byte[sdatao.Length];//list.ConvertAll(x => (byte)x).ToArray();
						for(int i = 0; i < nonceo.Length; i++) {
							object v = nonceo[i];
							byte b = Convert.ToByte(v);
							nonce[i] = b;
						}
						for(int i = 0; i < sdatao.Length; i++) {
							object v = sdatao[i];
							byte b = Convert.ToByte(v);
							sdata[i] = b;
						}
						HMACBlake2B encoder = new HMACBlake2B(null, 32 * 8);//param is bits
						byte[] sk = encoder.ComputeHash(keys.EncryptionKey);
						byte[] unsealed = Sodium.SecretBox.Open(sdata, nonce, sk);
						string json = Encoding.Default.GetString(unsealed);
						Dictionary<string, object> raw3 = SimpleJson.DeserializeObject<Dictionary<string, object>>(json);
						if(raw3.ContainsKey("keys")) {

						}
					}
				}
			}

			return store;
		}
	}
}