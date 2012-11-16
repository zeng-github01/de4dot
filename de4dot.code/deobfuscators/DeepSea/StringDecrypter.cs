﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Text;
using dot10.DotNet;
using dot10.DotNet.Emit;
using de4dot.blocks;

namespace de4dot.code.deobfuscators.DeepSea {
	class StringDecrypter {
		ModuleDefMD module;
		MethodDefinitionAndDeclaringTypeDict<IDecrypterInfo> methodToInfo = new MethodDefinitionAndDeclaringTypeDict<IDecrypterInfo>();
		DecrypterVersion version = DecrypterVersion.Unknown;

		public enum DecrypterVersion {
			Unknown,
			V1_3,
			V4_0,
			V4_1,
		}

		interface IDecrypterInfo {
			DecrypterVersion Version { get; }
			MethodDef Method { get; }
			string decrypt(object[] args);
			void cleanup();
		}

		static short[] findKey(MethodDef initMethod, FieldDef keyField) {
			var fields = new FieldDefinitionAndDeclaringTypeDict<bool>();
			fields.add(keyField, true);
			return findKey(initMethod, fields);
		}

		static short[] findKey(MethodDef initMethod, FieldDefinitionAndDeclaringTypeDict<bool> fields) {
			var instrs = initMethod.Body.Instructions;
			for (int i = 0; i < instrs.Count - 2; i++) {
				var ldci4 = instrs[i];
				if (!ldci4.IsLdcI4())
					continue;
				var newarr = instrs[i + 1];
				if (newarr.OpCode.Code != Code.Newarr)
					continue;
				if (newarr.Operand.ToString() != "System.Char")
					continue;

				var stloc = instrs[i + 2];
				if (!stloc.IsStloc())
					continue;
				var local = stloc.GetLocal(initMethod.Body.LocalList);

				int startInitIndex = i;
				i++;
				var array = ArrayFinder.getInitializedInt16Array(ldci4.GetLdcI4Value(), initMethod, ref i);
				if (array == null)
					continue;

				var field = getStoreField(initMethod, startInitIndex, local);
				if (field == null)
					continue;
				if (fields.find(field))
					return array;
			}

			return null;
		}

		static FieldDef getStoreField(MethodDef method, int startIndex, Local local) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldloc = instrs[i];
				if (!ldloc.IsLdloc())
					continue;
				if (ldloc.GetLocal(method.Body.LocalList) != local)
					continue;

				var stsfld = instrs[i + 1];
				if (stsfld.OpCode.Code != Code.Stsfld)
					continue;
				return stsfld.Operand as FieldDef;
			}

			return null;
		}

		static bool findMagic(MethodDef method, out int magic) {
			int arg1, arg2;
			return findMagic(method, out arg1, out arg2, out magic);
		}

		static bool findMagic(MethodDef method, out int arg1, out int arg2, out int magic) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 3; i++) {
				if ((arg1 = instrs[i].GetParameterIndex()) < 0)
					continue;
				var ldci4 = instrs[i + 1];
				if (!ldci4.IsLdcI4())
					continue;
				if (instrs[i + 2].OpCode.Code != Code.Xor)
					continue;
				if ((arg2 = instrs[i + 3].GetParameterIndex()) < 0)
					continue;
				magic = ldci4.GetLdcI4Value();
				return true;
			}
			arg1 = arg2 = 0;
			magic = 0;
			return false;
		}

		static void removeInitializeArrayCall(MethodDef method, FieldDef field) {
			var instrs = method.Body.Instructions;
			for (int i = 0; i < instrs.Count - 1; i++) {
				var ldtoken = instrs[i];
				if (ldtoken.OpCode.Code != Code.Ldtoken)
					continue;
				if (ldtoken.Operand != field)
					continue;

				var call = instrs[i + 1];
				if (call.OpCode.Code != Code.Call)
					continue;
				var calledMethod = call.Operand as IMethod;
				if (calledMethod == null)
					continue;
				if (calledMethod.ToString() != "System.Void System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(System.Array,System.RuntimeFieldHandle)")
					continue;

				instrs[i] = Instruction.Create(OpCodes.Pop);
				instrs[i + 1] = Instruction.Create(OpCodes.Nop);
			}
		}

		class DecrypterInfo41 : IDecrypterInfo {
			MethodDef cctor;
			int magic;
			int arg1, arg2;
			FieldDefinitionAndDeclaringTypeDict<bool> fields;
			ArrayInfo arrayInfo;
			ushort[] encryptedData;
			short[] key;
			int keyShift;
			bool isTrial;

			class ArrayInfo {
				public int sizeInElems;
				public ITypeDefOrRef elementType;
				public FieldDef initField;
				public FieldDef field;

				public ArrayInfo(int sizeInElems, ITypeDefOrRef elementType, FieldDef initField, FieldDef field) {
					this.sizeInElems = sizeInElems;
					this.elementType = elementType;
					this.initField = initField;
					this.field = field;
				}
			}

			public DecrypterVersion Version {
				get { return DecrypterVersion.V4_1; }
			}

			public MethodDef Method { get; private set; }

			public DecrypterInfo41(MethodDef cctor, MethodDef method) {
				this.cctor = cctor;
				Method = method;
			}

			public static bool isPossibleDecrypterMethod(MethodDef method, bool firstTime) {
				if (!checkMethodSignature(method))
					return false;
				var fields = getFields(method);
				if (fields == null || fields.Count != 3)
					return false;

				return true;
			}

			static bool checkMethodSignature(MethodDef method) {
				if (method.MethodSig.GetRetType().GetElementType() != ElementType.String)
					return false;
				int count = 0;
				foreach (var arg in method.MethodSig.GetParams()) {
					if (arg.ElementType == ElementType.I4)
						count++;
				}
				return count >= 2;
			}

			static FieldDefinitionAndDeclaringTypeDict<bool> getFields(MethodDef method) {
				var fields = new FieldDefinitionAndDeclaringTypeDict<bool>();
				foreach (var instr in method.Body.Instructions) {
					if (instr.OpCode.Code != Code.Ldsfld && instr.OpCode.Code != Code.Stsfld)
						continue;
					var field = instr.Operand as FieldDef;
					if (field == null)
						continue;
					if (field.DeclaringType != method.DeclaringType)
						continue;
					fields.add(field, true);
				}
				return fields;
			}

			public bool initialize() {
				if (!findMagic(Method, out arg1, out arg2, out magic))
					return false;

				fields = getFields(Method);
				if (fields == null)
					return false;

				arrayInfo = getArrayInfo(cctor);
				if (arrayInfo == null)
					return false;

				if (arrayInfo.initField.InitialValue.Length % 2 == 1)
					return false;
				encryptedData = new ushort[arrayInfo.initField.InitialValue.Length / 2];
				Buffer.BlockCopy(arrayInfo.initField.InitialValue, 0, encryptedData, 0, arrayInfo.initField.InitialValue.Length);

				isTrial = !DeobUtils.hasInteger(Method, 0xFFF0);
				keyShift = findKeyShift(cctor);
				key = findKey();
				if (key == null || key.Length == 0)
					return false;

				return true;
			}

			int findKeyShift(MethodDef method) {
				var instrs = method.Body.Instructions;
				for (int i = 0; i < instrs.Count - 3; i++) {
					int index = i;

					var ldci4 = instrs[index++];
					if (!ldci4.IsLdcI4())
						continue;
					if (ldci4.GetLdcI4Value() != 0xFF)
						continue;

					if (instrs[index++].OpCode.Code != Code.And)
						continue;
					if (instrs[index++].OpCode.Code != Code.Dup)
						continue;

					var ldci4_2 = instrs[index++];
					if (!ldci4_2.IsLdcI4())
						continue;

					if (findNextFieldUse(method, index) < 0)
						continue;

					return ldci4_2.GetLdcI4Value();
				}
				return -1;
			}

			int findNextFieldUse(MethodDef method, int index) {
				var instrs = method.Body.Instructions;
				for (int i = index; i < instrs.Count; i++) {
					var instr = instrs[i];
					if (instr.OpCode.Code != Code.Ldsfld && instr.OpCode.Code != Code.Stsfld)
						continue;
					var field = instr.Operand as IField;
					if (!fields.find(field))
						return -1;

					return i;
				}
				return -1;
			}

			ArrayInfo getArrayInfo(MethodDef method) {
				var instructions = method.Body.Instructions;
				for (int i = 0; i < instructions.Count; i++) {
					var ldci4_arraySizeInBytes = instructions[i];
					if (!ldci4_arraySizeInBytes.IsLdcI4())
						continue;
					i++;
					var instrs = DotNetUtils.getInstructions(instructions, i, OpCodes.Newarr, OpCodes.Dup, OpCodes.Ldtoken, OpCodes.Call, OpCodes.Stsfld);
					if (instrs == null)
						continue;

					int sizeInBytes = ldci4_arraySizeInBytes.GetLdcI4Value();
					var elementType = instrs[0].Operand as ITypeDefOrRef;
					var initField = instrs[2].Operand as FieldDef;
					var field = instrs[4].Operand as FieldDef;
					if (elementType == null)
						continue;
					if (initField == null || initField.InitialValue == null || initField.InitialValue.Length == 0)
						continue;
					if (!fields.find(field))
						continue;

					return new ArrayInfo(sizeInBytes, elementType, initField, field);
				}
				return null;
			}

			short[] findKey() {
				if (cctor.Module.Assembly == null)
					return null;
				var pkt = cctor.Module.Assembly.PublicKeyToken;
				if (!PublicKeyBase.IsNullOrEmpty2(pkt))
					return getPublicKeyTokenKey(pkt.Data);
				return findKey(cctor);
			}

			short[] findKey(MethodDef initMethod) {
				return StringDecrypter.findKey(initMethod, fields);
			}

			short[] getPublicKeyTokenKey(byte[] publicKeyToken) {
				if (keyShift < 0)
					throw new ApplicationException("Could not find shift value");
				var key = new short[publicKeyToken.Length];
				for (int i = 0; i < publicKeyToken.Length; i++) {
					int b = publicKeyToken[i];
					key[i] = (short)((b << keyShift) ^ b);
				}
				return key;
			}

			public string decrypt(object[] args) {
				if (isTrial)
					return decryptTrial((int)args[arg1], (int)args[arg2]);
				return decryptRetail((int)args[arg1], (int)args[arg2]);
			}

			string decryptTrial(int magic2, int magic3) {
				int offset = magic ^ magic2 ^ magic3;
				var keyChar = encryptedData[offset + 1];
				int cachedIndex = encryptedData[offset] ^ keyChar;
				int numChars = ((keyChar ^ encryptedData[offset + 2]) << 16) + (keyChar ^ encryptedData[offset + 3]);
				offset += 4;
				var sb = new StringBuilder(numChars);
				for (int i = 0; i < numChars; i++)
					sb.Append((char)(keyChar ^ encryptedData[offset + i] ^ key[(offset + i) % key.Length]));
				return sb.ToString();
			}

			string decryptRetail(int magic2, int magic3) {
				int offset = magic ^ magic2 ^ magic3;
				var keyChar = encryptedData[offset + 2];
				int cachedIndex = encryptedData[offset + 1] ^ keyChar;
				int flags = encryptedData[offset] ^ keyChar;
				int numChars = (flags >> 1) & ~7 | (flags & 7);
				if ((flags & 8) != 0) {
					numChars <<= 15;
					numChars |= encryptedData[offset + 3] ^ keyChar;
					offset++;
				}
				offset += 3;
				var sb = new StringBuilder(numChars);
				for (int i = 0; i < numChars; i++)
					sb.Append((char)(keyChar ^ encryptedData[offset + numChars - i - 1] ^ key[(i + 1 + offset) % key.Length]));
				return sb.ToString();
			}

			public void cleanup() {
				arrayInfo.initField.InitialValue = new byte[1];
				arrayInfo.initField.FieldSig.Type = arrayInfo.initField.Module.CorLibTypes.Byte;
				removeInitializeArrayCall(cctor, arrayInfo.initField);
			}
		}

		class DecrypterInfo40 : IDecrypterInfo {
			MethodDef cctor;
			int magic;
			FieldDef cachedStringsField;
			FieldDef keyField;
			FieldDef encryptedStringsField;
			FieldDef encryptedDataField;
			short[] key;
			ushort[] encryptedData;

			public MethodDef Method { get; private set; }

			public DecrypterVersion Version {
				get { return DecrypterVersion.V4_0; }
			}

			public DecrypterInfo40(MethodDef cctor, MethodDef method) {
				this.cctor = cctor;
				this.Method = method;
			}

			public static bool isPossibleDecrypterMethod(MethodDef method, bool firstTime) {
				if (!firstTime || !checkFields(method.DeclaringType.Fields))
					return false;
				return DotNetUtils.isMethod(method, "System.String", "(System.Int32,System.Int32)");
			}

			public bool initialize() {
				if (!findMagic(Method, out magic))
					return false;

				var charArrayFields = findFields();
				if (charArrayFields == null || charArrayFields.Count != 2)
					return false;

				encryptedStringsField = findEncryptedStrings(cctor, charArrayFields, out encryptedDataField);
				if (encryptedStringsField == null)
					return false;
				if (encryptedDataField.InitialValue.Length % 2 == 1)
					return false;
				encryptedData = new ushort[encryptedDataField.InitialValue.Length / 2];
				Buffer.BlockCopy(encryptedDataField.InitialValue, 0, encryptedData, 0, encryptedDataField.InitialValue.Length);

				charArrayFields.Remove(encryptedStringsField);
				keyField = charArrayFields[0];

				key = findKey();
				if (key == null || key.Length == 0)
					return false;

				return true;
			}

			List<FieldDef> findFields() {
				var charArrayFields = new List<FieldDef>();

				foreach (var instr in Method.Body.Instructions) {
					if (instr.OpCode.Code != Code.Stsfld && instr.OpCode.Code != Code.Ldsfld)
						continue;
					var field = instr.Operand as FieldDef;
					if (field == null)
						continue;
					if (!new SigComparer().Equals(Method.DeclaringType, field.DeclaringType))
						continue;
					switch (field.FieldSig.GetFieldType().GetFullName()) {
					case "System.Char[]":
						if (!charArrayFields.Contains(field))
							charArrayFields.Add(field);
						break;

					case "System.String[]":
						if (cachedStringsField != null && cachedStringsField != field)
							return null;
						cachedStringsField = field;
						break;

					default:
						break;
					}
				}

				if (cachedStringsField == null)
					return null;

				return charArrayFields;
			}

			static FieldDef findEncryptedStrings(MethodDef initMethod, List<FieldDef> ourFields, out FieldDef dataField) {
				for (int i = 0; i < initMethod.Body.Instructions.Count; i++) {
					var instrs = DotNetUtils.getInstructions(initMethod.Body.Instructions, i, OpCodes.Ldtoken, OpCodes.Call, OpCodes.Stsfld);
					if (instrs == null)
						continue;

					dataField = instrs[0].Operand as FieldDef;
					if (dataField == null || dataField.InitialValue == null || dataField.InitialValue.Length == 0)
						continue;

					var savedField = instrs[2].Operand as FieldDef;
					if (savedField == null || !matches(ourFields, savedField))
						continue;

					return savedField;
				}

				dataField = null;
				return null;
			}

			static bool matches(IEnumerable<FieldDef> ourFields, FieldDef field) {
				foreach (var ourField in ourFields) {
					if (FieldEqualityComparer.CompareDeclaringTypes.Equals(ourField, field))
						return true;
				}
				return false;
			}

			short[] findKey() {
				if (cctor.Module.Assembly == null)
					return null;
				var pkt = cctor.Module.Assembly.PublicKeyToken;
				if (!PublicKeyBase.IsNullOrEmpty2(pkt))
					return getPublicKeyTokenKey(pkt.Data);
				return findKey(cctor);
			}

			short[] findKey(MethodDef initMethod) {
				return StringDecrypter.findKey(initMethod, keyField);
			}

			static short[] getPublicKeyTokenKey(byte[] publicKeyToken) {
				var key = new short[publicKeyToken.Length];
				for (int i = 0; i < publicKeyToken.Length; i++) {
					int b = publicKeyToken[i];
					key[i] = (short)((b << 4) ^ b);
				}
				return key;
			}

			public string decrypt(object[] args) {
				return decrypt((int)args[0], (int)args[1]);
			}

			string decrypt(int magic2, int magic3) {
				int index = magic ^ magic2 ^ magic3;
				int cachedIndex = encryptedData[index++];
				int stringLen = encryptedData[index++] + ((int)encryptedData[index++] << 16);
				var sb = new StringBuilder(stringLen);
				for (int i = 0; i < stringLen; i++)
					sb.Append((char)(encryptedData[index++] ^ key[cachedIndex++ % key.Length]));
				return sb.ToString();
			}

			public void cleanup() {
				encryptedDataField.InitialValue = new byte[1];
				encryptedDataField.FieldSig.Type = encryptedDataField.Module.CorLibTypes.Byte;
				removeInitializeArrayCall(cctor, encryptedDataField);
			}
		}

		class DecrypterInfo13 : IDecrypterInfo {
			MethodDef cctor;
			FieldDef cachedStringsField;
			FieldDef keyField;
			int magic;
			string[] encryptedStrings;
			short[] key;

			public MethodDef Method { get; private set; }

			public DecrypterVersion Version {
				get { return DecrypterVersion.V1_3; }
			}

			public static bool isPossibleDecrypterMethod(MethodDef method, bool firstTime) {
				if (!firstTime || !checkFields(method.DeclaringType.Fields))
					return false;
				return DotNetUtils.isMethod(method, "System.String", "(System.Int32)");
			}

			public DecrypterInfo13(MethodDef cctor, MethodDef method) {
				this.cctor = cctor;
				this.Method = method;
			}

			public string decrypt(object[] args) {
				return decrypt((int)args[0]);
			}

			string decrypt(int magic2) {
				var es = encryptedStrings[magic ^ magic2];
				var sb = new StringBuilder(es.Length);
				for (int i = 0; i < es.Length; i++)
					sb.Append((char)(es[i] ^ key[(magic2 + i) % key.Length]));
				return sb.ToString();
			}

			public bool initialize() {
				if (!findMagic(Method, out magic))
					return false;

				if (!findFields())
					return false;

				if (!findEncryptedStrings(Method))
					return false;

				key = findKey();
				if (key == null || key.Length == 0)
					return false;

				return true;
			}

			bool findFields() {
				foreach (var instr in Method.Body.Instructions) {
					if (instr.OpCode.Code != Code.Stsfld && instr.OpCode.Code != Code.Ldsfld)
						continue;
					var field = instr.Operand as FieldDef;
					if (field == null)
						continue;
					if (!new SigComparer().Equals(Method.DeclaringType, field.DeclaringType))
						continue;
					switch (field.FieldSig.GetFieldType().GetFullName()) {
					case "System.Char[]":
						if (keyField != null && keyField != field)
							return false;
						keyField = field;
						break;

					case "System.String[]":
						if (cachedStringsField != null && cachedStringsField != field)
							return false;
						cachedStringsField = field;
						break;

					default:
						break;
					}
				}

				return keyField != null && cachedStringsField != null;
			}

			short[] findKey() {
				if (cctor.Module.Assembly == null)
					return null;
				var pkt = cctor.Module.Assembly.PublicKeyToken;
				if (!PublicKeyBase.IsNullOrEmpty2(pkt))
					return getPublicKeyTokenKey(pkt.Data);
				return findKey(cctor);
			}

			short[] findKey(MethodDef initMethod) {
				return StringDecrypter.findKey(initMethod, keyField);
			}

			static short[] getPublicKeyTokenKey(byte[] publicKeyToken) {
				var key = new short[publicKeyToken.Length];
				for (int i = 0; i < publicKeyToken.Length; i++) {
					int b = publicKeyToken[i];
					key[i] = (short)((b << 4) ^ b);
				}
				return key;
			}

			static bool findMagic(MethodDef method, out int magic) {
				var instrs = method.Body.Instructions;
				for (int i = 0; i < instrs.Count - 2; i++) {
					var ldarg = instrs[i];
					if (ldarg.GetParameterIndex() < 0)
						continue;
					var ldci4 = instrs[i + 1];
					if (!ldci4.IsLdcI4())
						continue;
					if (instrs[i + 2].OpCode.Code != Code.Xor)
						continue;
					magic = ldci4.GetLdcI4Value();
					return true;
				}
				magic = 0;
				return false;
			}

			bool findEncryptedStrings(MethodDef method) {
				var switchInstr = getOnlySwitchInstruction(method);
				if (switchInstr == null)
					return false;
				var targets = (Instruction[])switchInstr.Operand;
				encryptedStrings = new string[targets.Length];
				for (int i = 0; i < targets.Length; i++) {
					var target = targets[i];
					if (target.OpCode.Code != Code.Ldstr)
						return false;
					encryptedStrings[i] = (string)target.Operand;
				}
				return true;
			}

			static Instruction getOnlySwitchInstruction(MethodDef method) {
				Instruction switchInstr = null;
				foreach (var instr in method.Body.Instructions) {
					if (instr.OpCode.Code != Code.Switch)
						continue;
					if (switchInstr != null)
						return null;
					switchInstr = instr;
				}
				return switchInstr;
			}

			public void cleanup() {
			}

			public override string ToString() {
				return string.Format("M:{0:X8} N:{1}", magic, encryptedStrings.Length);
			}
		}

		public bool Detected {
			get { return methodToInfo.Count != 0; }
		}

		public DecrypterVersion Version {
			get { return version; }
		}

		public List<MethodDef> DecrypterMethods {
			get {
				var methods = new List<MethodDef>(methodToInfo.Count);
				foreach (var info in methodToInfo.getValues())
					methods.Add(info.Method);
				return methods;
			}
		}

		public StringDecrypter(ModuleDefMD module) {
			this.module = module;
		}

		public void find(ISimpleDeobfuscator simpleDeobfuscator) {
			if (module.Assembly == null)
				return;

			var pkt = module.Assembly.PublicKeyToken;
			bool hasPublicKeyToken = !PublicKeyBase.IsNullOrEmpty2(pkt);
			foreach (var type in module.GetTypes()) {
				var cctor = type.FindStaticConstructor();
				if (cctor == null)
					continue;

				bool deobfuscatedCctor = false;
				bool firstTime = true;
				foreach (var method in type.Methods) {
					if (!method.IsStatic || method.Body == null)
						continue;

					IDecrypterInfo info = null;

					if (DecrypterInfo13.isPossibleDecrypterMethod(method, firstTime)) {
						deobfuscateCctor(simpleDeobfuscator, cctor, ref deobfuscatedCctor, hasPublicKeyToken);
						simpleDeobfuscator.deobfuscate(method);
						info = getInfoV13(cctor, method);
					}
					else if (DecrypterInfo40.isPossibleDecrypterMethod(method, firstTime)) {
						deobfuscateCctor(simpleDeobfuscator, cctor, ref deobfuscatedCctor, hasPublicKeyToken);
						simpleDeobfuscator.deobfuscate(method);
						info = getInfoV40(cctor, method);
					}
					else if (DecrypterInfo41.isPossibleDecrypterMethod(method, firstTime)) {
						deobfuscateCctor(simpleDeobfuscator, cctor, ref deobfuscatedCctor, hasPublicKeyToken);
						simpleDeobfuscator.deobfuscate(method);
						info = getInfoV41(cctor, method);
					}
					firstTime = false;

					if (info == null)
						continue;
					methodToInfo.add(method, info);
					version = info.Version;
				}
			}
		}

		static void deobfuscateCctor(ISimpleDeobfuscator simpleDeobfuscator, MethodDef cctor, ref bool deobfuscatedCctor, bool hasPublicKeyToken) {
			if (deobfuscatedCctor || hasPublicKeyToken)
				return;
			simpleDeobfuscator.deobfuscate(cctor);
			deobfuscatedCctor = true;
		}

		static bool checkFields(IEnumerable<FieldDef> fields) {
			bool foundCharAry = false, foundStringAry = false;
			foreach (var field in fields) {
				if (foundCharAry && foundStringAry)
					break;
				switch (field.FieldSig.GetFieldType().GetFullName()) {
				case "System.Char[]":
					foundCharAry = true;
					break;
				case "System.String[]":
					foundStringAry = true;
					break;
				}
			}
			return foundCharAry && foundStringAry;
		}

		DecrypterInfo13 getInfoV13(MethodDef cctor, MethodDef method) {
			var info = new DecrypterInfo13(cctor, method);
			if (!info.initialize())
				return null;
			return info;
		}

		DecrypterInfo40 getInfoV40(MethodDef cctor, MethodDef method) {
			var info = new DecrypterInfo40(cctor, method);
			if (!info.initialize())
				return null;
			return info;
		}

		DecrypterInfo41 getInfoV41(MethodDef cctor, MethodDef method) {
			var info = new DecrypterInfo41(cctor, method);
			if (!info.initialize())
				return null;
			return info;
		}

		public string decrypt(IMethod method, object[] args) {
			var info = methodToInfo.find(method);
			return info.decrypt(args);
		}

		public void cleanup() {
			foreach (var info in methodToInfo.getValues())
				info.cleanup();
		}
	}
}
