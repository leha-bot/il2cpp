﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using il2cpp;

namespace test
{
	internal class Testbed
	{
		public string TestDir;
		public Action<Il2cppContext, TypeDef, string, string, string> OnType;

		public void Start()
		{
			var files = Directory.GetFiles(TestDir, "*.exe", SearchOption.AllDirectories);
			foreach (string file in files)
			{
				string dir = Path.GetDirectoryName(file);
				TestAssembly(dir, file);
			}
		}

		private void TestAssembly(string imageDir, string imagePath)
		{
			string imageName = Path.GetFileName(imagePath);
			string subDir = GetRelativePath(imageDir, TestDir);

			try
			{
				Il2cppContext context = new Il2cppContext(imagePath);

				foreach (TypeDef typeDef in context.Module.Types)
				{
					OnType(context, typeDef, imageDir, imageName, subDir);
				}
			}
			catch (BadImageFormatException)
			{
				Console.WriteLine("* Load Error: \"{0}{1}\" is not a .NET assembly.", subDir, imageName);
			}
		}

		private static string GetRelativePath(string path, string relativeTo)
		{
			string fullPath = Path.GetFullPath(path);

			if (string.IsNullOrEmpty(relativeTo))
				relativeTo = ".";

			string fullRelative = Path.GetFullPath(relativeTo + '/');
			if (fullPath.Length >= fullRelative.Length)
				return fullPath.Substring(fullRelative.Length);
			else
				return path;
		}
	}

	internal class Program
	{
		private static int TotalTests;
		private static int PassedTests;

		private static MethodDef IsTestBinding(TypeDef typeDef)
		{
			if (typeDef.HasCustomAttributes)
			{
				var attr = typeDef.CustomAttributes[0];
				if (attr.AttributeType.Name == "TestAttribute")
				{
					return typeDef.FindMethod("Entry");
				}
			}
			else
			{
				MethodDef entryPoint = typeDef.Module.EntryPoint;
				if (entryPoint != null && entryPoint.DeclaringType == typeDef)
				{
					if (entryPoint.HasBody &&
						entryPoint.Body.Instructions.Count > 2)
						return entryPoint;
				}
			}
			return null;
		}

		private static void TestBinding(
			Il2cppContext context, TypeDef typeDef,
			string imageDir, string imageName, string subDir)
		{
			MethodDef metDef = IsTestBinding(typeDef);
			if (metDef == null)
				return;

			string testName = string.Format("[{0}]{1}", imageName, typeDef.FullName);
			var oldColor = Console.ForegroundColor;
			Console.Write("{0} {1}: ", subDir, testName);

			context.AddEntry(metDef);

			var sw = new Stopwatch();
			sw.Start();
			string exceptionMsg = null;
			try
			{
				context.Resolve();
			}
			catch (TypeLoadException ex)
			{
				exceptionMsg = ex.Message;
			}
			sw.Stop();
			long elapsedMS = sw.ElapsedMilliseconds;
			Console.Write("{0}ms, ", elapsedMS);

			StringBuilder sb = new StringBuilder();
			if (exceptionMsg != null)
				sb.Append(exceptionMsg);
			else
			{
				HierarchyDump dumper = new HierarchyDump(context);

				/*sb.Append("* MethodTables:\n");
				dumper.DumpMethodTables(sb);*/
				sb.Append("* Types:\n");
				dumper.DumpTypes(sb);
			}

			var dumpData = Encoding.UTF8.GetBytes(sb.ToString());

			string validatedName = ValidatePath(testName);
			File.WriteAllBytes(
				Path.Combine(imageDir, validatedName + ".dump"),
				dumpData);

			byte[] cmpData = null;
			try
			{
				cmpData = File.ReadAllBytes(Path.Combine(imageDir, validatedName + ".txt"));
				cmpData = ReplaceNewLines(cmpData);
			}
			catch
			{
			}

			if (cmpData != null && dumpData.SequenceEqual(cmpData))
			{
				Console.ForegroundColor = ConsoleColor.Green;
				Console.WriteLine("PASS");
				Console.ForegroundColor = oldColor;

				++PassedTests;
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("FAIL");
				Console.ForegroundColor = oldColor;
			}

			++TotalTests;

			context.Reset();
		}

		private static MethodDef IsTestCodeGen(TypeDef typeDef)
		{
			if (typeDef.HasCustomAttributes)
			{
				var attr = typeDef.CustomAttributes[0];
				if (attr.AttributeType.Name == "CodeGenAttribute")
				{
					return typeDef.FindMethod("Entry");
				}
			}
			return null;
		}

		private static void TestCodeGen(
			Il2cppContext context, TypeDef typeDef,
			string imageDir, string imageName, string subDir)
		{
			MethodDef metDef = IsTestCodeGen(typeDef);
			if (metDef == null)
				return;

			string testName = string.Format("[{0}]{1}", imageName, typeDef.FullName);
			var oldColor = Console.ForegroundColor;
			Console.Write("{0} {1}: ", subDir, testName);

			context.AddEntry(metDef);

			var sw = new Stopwatch();
			sw.Start();
			string exceptionMsg = null;
			try
			{
				context.Resolve();
			}
			catch (TypeLoadException ex)
			{
				exceptionMsg = ex.Message;
			}
			sw.Stop();
			long elapsedMS = sw.ElapsedMilliseconds;
			Console.Write("Res({0}ms) ", elapsedMS);

			sw.Restart();
			var genResult = context.Generate();
			sw.Stop();
			elapsedMS = sw.ElapsedMilliseconds;
			Console.Write("Gen({0}ms) ", elapsedMS);

			var mainUnit = new CompileUnit();
			genResult.UnitList.Add(mainUnit);
			mainUnit.Name = "main";
			string metName = genResult.GetMethodName(metDef, out var metUnitName);
			mainUnit.ImplDepends.Add(metUnitName);
			mainUnit.ImplCode =
				"#include <stdio.h>\n" +
				"#include <time.h>\n" +
				"#include <string>\n\n" +
				"int main()\n" +
				"{\n" +
				"	il2cpp_Init();\n" +
				"	auto start = clock();\n" +
				"	auto result = " + metName + "();\n" +
				"	auto elapsed = clock() - start;\n" +
				"	printf(\"Result(%s), %ldms\", std::to_string(result).c_str(), elapsed);\n" +
				"	return 0;\n" +
				"}\n";

			genResult.GenerateIncludes();

			string validatedName = ValidatePath(testName);
			string genDir = Path.Combine(imageDir, "../../gen/", validatedName);
			Il2cppContext.SaveToFolder(
				genDir,
				genResult.UnitList);
			genDir = Path.GetFullPath(genDir);

			Console.Write("Building");
			bool hasBuildErr = false;
			Action<string> actOutput = strOut =>
			{
				if (!hasBuildErr)
				{
					if (strOut.IndexOf("error") != -1)
					{
						Console.WriteLine();
						hasBuildErr = true;
					}
					else if (strOut.IndexOf("Compiled:") != -1)
						Console.Write(".");
				}

				if (hasBuildErr)
				{
					Console.Error.WriteLine("{0}", strOut);
				}
			};

			RunCommand(
				null,
				"build.cmd",
				genDir,
				actOutput,
				actOutput);

			string result = null;
			if (!hasBuildErr)
			{
				Console.Write(" Running");
				string runOutput = null;
				RunCommand(
					null,
					"final.exe",
					genDir,
					strOut => runOutput = strOut,
					Console.Error.WriteLine);

				Console.Write(" {0} ", runOutput);

				result = GetRunResult(runOutput);
			}

			if (result == "0")
			{
				Console.ForegroundColor = ConsoleColor.Green;
				Console.WriteLine("PASS");
				Console.ForegroundColor = oldColor;

				++PassedTests;
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("FAIL");
				Console.ForegroundColor = oldColor;
			}

			context.Reset();
		}

		private static string GetRunResult(string str)
		{
			int lp = str.IndexOf('(');
			if (lp == -1)
				return null;
			++lp;
			int rp = str.IndexOf(')', lp);
			if (rp == -1)
				return null;

			return str.Substring(lp, rp - lp);
		}

		private static byte[] ReplaceNewLines(byte[] data)
		{
			int len = data.Length;
			int writePtr = 0;
			int readPtr = 0;
			for (; readPtr < len; ++writePtr, ++readPtr)
			{
				byte curr = data[writePtr] = data[readPtr];
				if (curr == '\r')
				{
					int nextPtr = readPtr + 1;
					if (nextPtr < len)
					{
						byte next = data[nextPtr];
						if (next == '\n')
						{
							data[writePtr] = (byte)'\n';
							++readPtr;
						}
					}
					else
						break;
				}
			}

			int offset = readPtr - writePtr;
			if (offset > 0)
			{
				byte[] result = new byte[writePtr];
				Array.Copy(data, result, writePtr);
				return result;
			}
			else
				return data;
		}

		private static string ValidatePath(string str)
		{
			StringBuilder sb = new StringBuilder();
			foreach (var ch in str)
			{
				if (ch == '<' || ch == '>')
					sb.Append('_');
				else
					sb.Append(ch);
			}
			return sb.ToString();
		}

		private static void RunCommand(
			string program,
			string arguments,
			string workDir,
			Action<string> onOutput,
			Action<string> onError,
			bool isWait = true)
		{
			if (program == null)
			{
				program = "cmd";
				arguments = "/c " + arguments;
			}

			var si = new ProcessStartInfo
			{
				WorkingDirectory = workDir,
				FileName = program,
				Arguments = arguments,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				UseShellExecute = false
			};
			// 此环境变量会破坏 clang 定位到正确的头文件目录
			si.EnvironmentVariables.Remove("VCInstallDir");

			Process pSpawn = new Process
			{
				StartInfo = si
			};

			if (onOutput != null)
			{
				pSpawn.OutputDataReceived += (sender, args) =>
				{
					if (args.Data != null)
						onOutput(args.Data);
				};
			}
			if (onError != null)
			{
				pSpawn.ErrorDataReceived += (sender, args) =>
				{
					if (args.Data != null)
						onError(args.Data);
				};
			}

			pSpawn.Start();

			if (onOutput != null)
				pSpawn.BeginOutputReadLine();

			if (onError != null)
				pSpawn.BeginErrorReadLine();

			if (isWait)
				pSpawn.WaitForExit();
		}

		private static void Main(string[] args)
		{
#if false
			var testBinding = new Testbed();
			testBinding.TestDir = "../../../testcases/";
			testBinding.OnType = TestBinding;
			testBinding.Start();
			Console.WriteLine("\nTestBinding Passed: {0}/{1}", PassedTests, TotalTests);
#else
			var testCodeGen = new Testbed();
			testCodeGen.TestDir = "../../../testcases/";
			testCodeGen.OnType = TestCodeGen;
			testCodeGen.Start();
#endif
		}
	}
}
