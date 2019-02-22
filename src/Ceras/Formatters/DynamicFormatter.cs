﻿
// ReSharper disable ArgumentsStyleOther
// ReSharper disable ArgumentsStyleNamedExpression
namespace Ceras.Formatters
{
	using System;
	using Helpers;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Linq.Expressions;
	using System.Reflection;
	using static System.Linq.Expressions.Expression;

#if FAST_EXP
	using FastExpressionCompiler;
#endif


	// todo: Can we use a static-generic as a cache instead of dict? Is that even possible in our case? Would we even save anything? How much would it be faster?
	/*
	 * This formatter is used for every object-type that Ceras cannot deal with.
	 * It analyzes the members of the class or struct and compiles an optimized formatter for it.
	 * 
	 * - "How does it handle abstract classes?"
	 * > The ReferenceFormatter<> does that by "dispatching" to the actual type at runtime, dispatching one of many different DynamicObjectForamtters.
	 * 
	 * - "Why does it not implement ISchemaTaintedFormatter?"
	 * > That concept only applies to types whos schema can change. So in VersionTolerant serialization both DynamicFormatter and SchemaDynamicFormatter are used.
	 *   This one is used for framework-types that are not supported, and SchemaDynamicFormatter is used for user-types.
	 *   Because user-types can change over time, and framework-types stay the same, and if they change that has to be dealt with in a completely different way anyway.
	 */

	// todo: what about some member-attributes for:
	// - using a specific formatter? (HalfFloat, Int32Fixed, MyUserFormatter)
	// - ignore caching (not using the reference formatter)

	// todo: merge constants
	// If there's an object that has multiple 'int' fields, then we would obtain multiple 'int' formatters which is bad.
	// Instead we could put them into a dictionary and lookup what formatter to use for what type, so after compiling there is only one instance per formatter

	// todo: access primitive writers directly
	// Instead of obtaining an 'Int32Formatter' and the like, we should compile a call directly to SerializerBinary.WriteInt32() ...
	// That would avoid quite some overhead: removing the vtable dispatch, enabling inlining!

	sealed class DynamicFormatter<T> : IFormatter<T>
	{
		readonly CerasSerializer _ceras;
		readonly SerializeDelegate<T> _dynamicSerializer;
		readonly DeserializeDelegate<T> _dynamicDeserializer;


		public DynamicFormatter(CerasSerializer serializer)
		{
			_ceras = serializer;

			var type = typeof(T);
			BannedTypes.ThrowIfNonspecific(type);

			var meta = _ceras.GetTypeMetaData(type);
			
			var typeConfig = _ceras.Config.GetTypeConfig(type);
			typeConfig.VerifyConstructionMethod();


			var schema = meta.PrimarySchema;
			if (schema.Members.Count > 0)
			{
				_dynamicSerializer = GenerateSerializer(schema);
				_dynamicDeserializer = GenerateDeserializer(schema);
			}
			else
			{
				_dynamicSerializer = (ref byte[] buffer, ref int offset, T value) => { };
				_dynamicDeserializer = (byte[] buffer, ref int offset, ref T value) => { };
			}
		}


		SerializeDelegate<T> GenerateSerializer(Schema schema)
		{
			var members = schema.Members;

			var refBufferArg = Parameter(typeof(byte[]).MakeByRefType(), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var valueArg = Parameter(typeof(T), "value");

			var block = new List<Expression>();


			foreach (var sMember in members)
			{
				var member = sMember;

				// todo: have a lookup list to directly get the actual 'SerializerBinary' method. There is no reason to actually use objects like "Int32Formatter" IF we can "unpack" them
				// todo: .. we could have a dictionary that maps formatter-types to MethodInfo, that would also make it so we don't even have to keep a constant with the reference to the formatter around
				// todo: depending on the configuration, we could have a "StructBlitFormatter" which just re-interprets the pointer and writes the data directly in a single assignment; like casting the byte[] to a byte* to a Vector3* and then doing a direct assignment. (only works with blittable types, and if the setting is active)
				// todo: if we have a setting for that, it should be global (as a fallback) as well as a per-type config; the TypeConfig has to get its default value from the global value (maybe in the CerasSerializer ctor), so it doesn't have to keep looking into the global config; and so there is no bug when someone configures some types directly first and then sets the default after!
				// todo: fully unpack known formatters as well. Maybe let matching formatters implement an interface that can return some sort of "Expression GetDirectCall(bufferArg, offsetArg, localStore)"
				var formatter = _ceras.GetReferenceFormatter(member.MemberType);

				// Get the formatter and its Serialize method
				// var formatter = _ceras.GetFormatter(fieldInfo.FieldType, extraErrorInformation: $"DynamicFormatter ObjectType: {specificType.FullName} FieldType: {fieldInfo.FieldType.FullName}");
				var serializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Serialize));
				Debug.Assert(serializeMethod != null, "Can't find serialize method on formatter " + formatter.GetType().FullName);

				// Access the field that we want to serialize
				var fieldExp = MakeMemberAccess(valueArg, member.MemberInfo);

				// Call "Serialize"
				var serializeCall = Call(Constant(formatter), serializeMethod, refBufferArg, refOffsetArg, fieldExp);
				block.Add(serializeCall);
			}

			var serializeBlock = Block(expressions: block);

#if FAST_EXP
			return Expression.Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).CompileFast(true);
#else
			return Lambda<SerializeDelegate<T>>(serializeBlock, refBufferArg, refOffsetArg, valueArg).Compile();
#endif

		}

		DeserializeDelegate<T> GenerateDeserializer(Schema schema)
		{
			var members = schema.Members;
			var typeConfig = _ceras.Config.GetTypeConfig(schema.Type);
			var tc = typeConfig.TypeConstruction;

			bool constructObject = tc.HasDataArguments; // Are we responsible for instantiating an object?
			HashSet<ParameterExpression> usedVariables = null;

			var bufferArg = Parameter(typeof(byte[]), "buffer");
			var refOffsetArg = Parameter(typeof(int).MakeByRefType(), "offset");
			var refValueArg = Parameter(typeof(T).MakeByRefType(), "value");

			var body = new List<Expression>();
			var locals = new List<ParameterExpression>(schema.Members.Count);

			//
			// 1. Read existing values into locals (Why? See explanation at the end of the file)
			for (var i = 0; i < members.Count; i++)
			{
				var member = members[i];

				// Read the data into a new local variable 
				var tempStore = Variable(member.MemberType, member.MemberName + "_local");
				locals.Add(tempStore);

				if (constructObject)
					continue; // Can't read existing data when 

				// Init the local with the current value
				body.Add(Assign(tempStore, MakeMemberAccess(refValueArg, member.MemberInfo)));
			}

			//
			// 2. Deserialize using local variable (faster and more robust than working with field/prop directly)
			for (var i = 0; i < members.Count; i++)
			{
				var member = members[i];
				var tempStore = locals[i];

				var formatter = _ceras.GetReferenceFormatter(member.MemberType);
				var deserializeMethod = formatter.GetType().GetMethod(nameof(IFormatter<int>.Deserialize));
				Debug.Assert(deserializeMethod != null, "Can't find deserialize method on formatter " + formatter.GetType().FullName);

				// Deserialize the data into the local
				// todo: fully unpack known formatters as well. Maybe let matching formatters implement an interface that can return some sort of "Expression GetDirectCall(bufferArg, offsetArg, localStore)"
				var tempReadCall = Call(Constant(formatter), deserializeMethod, bufferArg, refOffsetArg, tempStore);
				body.Add(tempReadCall);
			}

			//
			// 3. Create object instance (only when actually needed)
			if (constructObject)
			{
				// Create a helper array for the implementing type construction
				var memberParameters = schema.Members.Zip(locals, (m, l) => new MemberParameterPair {Member = m.MemberInfo, LocalVar = l}).ToArray();

				usedVariables = new HashSet<ParameterExpression>();
				tc.EmitConstruction(schema, body, refValueArg, usedVariables, memberParameters);
			}

			//
			// 4. Write back values in one batch
			for (int i = 0; i < members.Count; i++)
			{
				var member = members[i];
				var tempStore = locals[i];
				var type = member.MemberType;
				
				if (usedVariables != null && usedVariables.Contains(tempStore))
					// Member was already used in the constructor / factory method, no need to write it again
					continue;

				if (member.MemberInfo is FieldInfo fieldInfo)
				{
					if (fieldInfo.IsInitOnly)
					{
						// Readonly field
						var memberConfig = typeConfig.Members.First(m => m.Member == member.MemberInfo);
						var rh = memberConfig.ComputeReadonlyHandling();
						DynamicFormatterHelpers.EmitReadonlyWriteBack(type, rh, fieldInfo, refValueArg, tempStore, body);
					}
					else
					{
						// Normal assignment
						body.Add(Assign(left: Field(refValueArg, fieldInfo),
										right: tempStore));
					}
				}
				else
				{
					// Context
					var p = (PropertyInfo)member.MemberInfo;

					var setMethod = p.GetSetMethod(true);
					body.Add(Call(instance: refValueArg, setMethod, tempStore));
				}
			}


			var bodyBlock = Block(variables: locals, expressions: body);
#if FAST_EXP
			return Expression.Lambda<DeserializeDelegate<T>>(serializeBlock, bufferArg, refOffsetArg, refValueArg).CompileFast(true);
#else
			return Lambda<DeserializeDelegate<T>>(bodyBlock, bufferArg, refOffsetArg, refValueArg).Compile();
#endif
		}

		public void Serialize(ref byte[] buffer, ref int offset, T value) => _dynamicSerializer(ref buffer, ref offset, value);

		public void Deserialize(byte[] buffer, ref int offset, ref T value) => _dynamicDeserializer(buffer, ref offset, ref value);
	}
}