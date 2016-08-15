﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ApiCheck.Baseline;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ApiCheck
{
    public class BaselineGenerator
    {
        private const BindingFlags SearchFlags = BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Instance |
            BindingFlags.Static |
            BindingFlags.DeclaredOnly;

        private readonly Assembly _assembly;
        private readonly IEnumerable<Func<TypeInfo, bool>> _filters;

        public BaselineGenerator(Assembly assembly, IEnumerable<Func<TypeInfo, bool>> filters)
        {
            _assembly = assembly;
            _filters = filters;
        }

        public static JObject GenerateBaselineReport(Assembly assembly, IEnumerable<Func<TypeInfo, bool>> filters = null)
        {
            var generator = new BaselineGenerator(assembly, filters ?? Enumerable.Empty<Func<TypeInfo, bool>>());
            var baselineDocument = generator.GenerateBaseline();
            return JObject.FromObject(baselineDocument);
        }

        public BaselineDocument GenerateBaseline()
        {
            var types = _assembly.DefinedTypes;

            var document = new BaselineDocument();
            document.AssemblyIdentity = _assembly.GetName().ToString();

            foreach (var type in _assembly.DefinedTypes.Where(type => _filters.All(filter => filter(type))))
            {
                var baselineType = GenerateTypeBaseline(type);
                document.Types.Add(baselineType);
            }

            return document;
        }

        private TypeBaseline GenerateTypeBaseline(TypeInfo type)
        {
            var typeBaseline = new TypeBaseline();

            typeBaseline.Name = TypeBaseline.GetTypeNameFor(type);

            typeBaseline.Kind = type.IsInterface ? BaselineKind.Interface : type.IsValueType ? BaselineKind.Struct : BaselineKind.Class;

            typeBaseline.Visibility = type.IsPublic || type.IsNestedPublic ? BaselineVisibility.Public :
                type.IsNestedFamORAssem ? BaselineVisibility.ProtectedInternal :
                type.IsNestedFamily ? BaselineVisibility.Protected :
                type.IsNestedPrivate ? BaselineVisibility.Private :
                BaselineVisibility.Internal;

            typeBaseline.Static = typeBaseline.Kind == BaselineKind.Class && type.IsSealed && type.IsAbstract;

            typeBaseline.Abstract = type.IsAbstract;

            typeBaseline.Sealed = type.IsSealed;

            if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
            {
                typeBaseline.BaseType = TypeBaseline.GetTypeNameFor(type.BaseType.GetTypeInfo());
            }

            if (type.ImplementedInterfaces?.Count() > 0)
            {
                var interfaces = TypeBaseline.GetImplementedInterfacesFor(type);
                foreach (var @interface in interfaces.Select(i => TypeBaseline.GetTypeNameFor(i)))
                {
                    typeBaseline.ImplementedInterfaces.Add(@interface);
                }
            }

            if (type.IsGenericType)
            {
                var constraints = GetGenericConstraintsFor(type.GetGenericArguments().Select(t => t.GetTypeInfo()));
                foreach (var constraint in constraints)
                {
                    typeBaseline.GenericConstraints.Add(constraint);
                }
            }

            var members = type.GetMembers(SearchFlags);

            foreach (var member in members)
            {
                var memberBaseline = GenerateMemberBaseline(type, member);
                if (memberBaseline != null)
                {
                    typeBaseline.Members.Add(memberBaseline);
                }
            }

            return typeBaseline;
        }

        private static IEnumerable<GenericConstraintBaseline> GetGenericConstraintsFor(IEnumerable<TypeInfo> genericArguments)
        {
            foreach (var typeArgument in genericArguments)
            {
                var constraintBaseline = new GenericConstraintBaseline();

                if (typeArgument.BaseType != null &&
                    typeArgument.BaseType != typeof(object)
                    && typeArgument.BaseType != typeof(ValueType))
                {
                    constraintBaseline.BaseTypeOrInterfaces.Add(TypeBaseline.GetTypeNameFor(typeArgument.BaseType.GetTypeInfo()));
                }

                foreach (var interfaceType in TypeBaseline.GetImplementedInterfacesFor(typeArgument))
                {
                    constraintBaseline.BaseTypeOrInterfaces.Add(TypeBaseline.GetTypeNameFor(interfaceType));
                }

                constraintBaseline.ParameterName = typeArgument.Name;
                constraintBaseline.New = (typeArgument.GenericParameterAttributes & GenericParameterAttributes.DefaultConstructorConstraint) == GenericParameterAttributes.DefaultConstructorConstraint;
                constraintBaseline.Class = (typeArgument.GenericParameterAttributes & GenericParameterAttributes.ReferenceTypeConstraint) == GenericParameterAttributes.ReferenceTypeConstraint;
                constraintBaseline.Struct = (typeArgument.GenericParameterAttributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == GenericParameterAttributes.NotNullableValueTypeConstraint;

                if (constraintBaseline.New || constraintBaseline.Class || constraintBaseline.Struct || constraintBaseline.BaseTypeOrInterfaces.Count > 0)
                {
                    yield return constraintBaseline;
                }
            }
        }

        private MemberBaseline GenerateMemberBaseline(TypeInfo type, MemberInfo member)
        {
            switch (member.MemberType)
            {
                case MemberTypes.Constructor:
                    var ctor = (ConstructorInfo)member;
                    var constructorBaseline = new MemberBaseline();
                    constructorBaseline.Kind = MemberBaselineKind.Constructor;
                    constructorBaseline.Visibility = ctor.IsPublic ? BaselineVisibility.Public :
                        ctor.IsFamilyOrAssembly ? BaselineVisibility.ProtectedInternal :
                        ctor.IsFamily ? BaselineVisibility.Protected :
                        ctor.IsPrivate ? BaselineVisibility.Private : BaselineVisibility.Internal;

                    constructorBaseline.Name = MemberBaseline.GetMemberNameFor(ctor);
                    foreach (var parameter in ctor.GetParameters())
                    {
                        var parameterBaseline = GenerateParameterBaseline(parameter);
                        constructorBaseline.Parameters.Add(parameterBaseline);
                    }

                    return constructorBaseline;
                case MemberTypes.Method:
                    var name = member.Name;
                    var method = (MethodInfo)member;
                    var methodBaseline = new MemberBaseline();

                    methodBaseline.Kind = MemberBaselineKind.Method;

                    methodBaseline.Visibility = method.IsPublic ? BaselineVisibility.Public :
                        method.IsFamilyOrAssembly ? BaselineVisibility.ProtectedInternal :
                        method.IsFamily ? BaselineVisibility.Protected :
                        method.IsPrivate ? BaselineVisibility.Private : BaselineVisibility.Internal;

                    methodBaseline.ExplicitInterface = GetInterfaceImplementation(method, explicitImplementation: true);
                    methodBaseline.ImplementedInterface = methodBaseline.ExplicitInterface ?? GetInterfaceImplementation(method, explicitImplementation: false);
                    methodBaseline.Name = MemberBaseline.GetMemberNameFor(method);

                    if (method.IsGenericMethod)
                    {
                        var constraints = GetGenericConstraintsFor(method.GetGenericArguments().Select(t => t.GetTypeInfo()));
                        foreach (var constraint in constraints)
                        {
                            methodBaseline.GenericConstraints.Add(constraint);
                        }
                    }

                    methodBaseline.Static = method.IsStatic;
                    methodBaseline.Sealed = method.IsFinal;
                    methodBaseline.Virtual = method.IsVirtual;
                    methodBaseline.Override = method.IsVirtual && method.GetBaseDefinition() != method;
                    methodBaseline.Abstract = method.IsAbstract;
                    methodBaseline.New = !method.IsAbstract && !method.IsVirtual && method.IsHideBySig &&
                        method.DeclaringType.GetMember(method.Name).OfType<MethodInfo>()
                        .Where(m => SameSignature(m, method)).Count() > 1;
                    methodBaseline.Extension = method.IsDefined(typeof(ExtensionAttribute), false);

                    foreach (var parameter in method.GetParameters())
                    {
                        var parameterBaseline = GenerateParameterBaseline(parameter);
                        methodBaseline.Parameters.Add(parameterBaseline);
                    }

                    methodBaseline.ReturnType = TypeBaseline.GetTypeNameFor(method.ReturnType.GetTypeInfo());

                    return methodBaseline;
                case MemberTypes.Field:
                    var field = (FieldInfo)member;
                    var fieldBaseline = new MemberBaseline();

                    fieldBaseline.Visibility = field.IsPublic ? BaselineVisibility.Public :
                        field.IsFamilyOrAssembly ? BaselineVisibility.ProtectedInternal :
                        field.IsFamily ? BaselineVisibility.Protected :
                        field.IsPrivate ? BaselineVisibility.Private : BaselineVisibility.Internal;

                    fieldBaseline.Constant = field.IsLiteral;
                    fieldBaseline.Static = field.IsStatic;
                    fieldBaseline.ReadOnly = field.IsInitOnly;
                    fieldBaseline.Kind = MemberBaselineKind.Field;

                    fieldBaseline.Name = field.Name;
                    fieldBaseline.ReturnType = TypeBaseline.GetTypeNameFor(field.FieldType.GetTypeInfo());

                    return fieldBaseline;
                case MemberTypes.Event:
                case MemberTypes.Property:
                case MemberTypes.NestedType:
                    // All these cases are covered by the methods they implicitly define on the class
                    // (Properties and Events) and when we enumerate all the types in an assembly (Nested types).
                    return null;
                case MemberTypes.TypeInfo:
                // There should not be any member passsed into this method that is not a top level type.
                case MemberTypes.Custom:
                // We don't know about custom member types, so better throw if we find something we don't understand.
                case MemberTypes.All:
                    throw new InvalidOperationException($"'{type.MemberType}' [{member}] is not supported.");
                default:
                    return null;
            }
        }

        public static BaselineDocument LoadFrom(string path)
        {
            return JsonConvert.DeserializeObject<BaselineDocument>(File.ReadAllText(path));
        }

        private string GetInterfaceImplementation(MethodInfo method, bool explicitImplementation)
        {
            var typeInfo = method.DeclaringType.GetTypeInfo();
            foreach (var interfaceImplementation in method.DeclaringType.GetInterfaces())
            {
                var map = typeInfo.GetRuntimeInterfaceMap(interfaceImplementation);
                if (map.TargetMethods.Any(m => m.Equals(method)))
                {
                    return !explicitImplementation || (method.IsPrivate && method.IsFinal) ?
                        TypeBaseline.GetTypeNameFor(interfaceImplementation.GetTypeInfo()) :
                        null;
                }
            }

            return null;
        }

        private bool SameSignature(MethodInfo candidate, MethodInfo method)
        {
            if (candidate.ReturnType != method.ReturnType)
            {
                return false;
            }

            var candidateParameters = candidate.GetParameters();
            var methodParameters = method.GetParameters();

            if (candidateParameters.Length != methodParameters.Length)
            {
                return false;
            }

            for (int i = 0; i < candidateParameters.Length; i++)
            {
                var candidateParameter = candidateParameters[i];
                var methodParameter = methodParameters[i];
                if (candidateParameter.ParameterType != methodParameter.ParameterType ||
                    candidateParameter.HasDefaultValue != methodParameter.HasDefaultValue ||
                    candidateParameter.IsIn != methodParameter.IsIn ||
                    candidateParameter.IsOut != methodParameter.IsOut ||
                    candidateParameter.IsOptional != methodParameter.IsOptional)
                {
                    return false;
                }
            }

            return true;
        }

        private ParameterBaseline GenerateParameterBaseline(ParameterInfo parameter)
        {
            return new ParameterBaseline
            {
                Name = parameter.Name,
                Type = TypeBaseline.GetTypeNameFor(parameter.ParameterType.GetTypeInfo()),
                Direction = parameter.ParameterType.IsByRef && parameter.IsOut ? BaselineParameterDirection.Out :
                    parameter.ParameterType.IsByRef && !parameter.IsOut ? BaselineParameterDirection.Ref :
                    BaselineParameterDirection.In,
                DefaultValue = parameter.HasDefaultValue ? FormatDefaultValue(parameter) : null,
                IsParams = parameter.GetCustomAttribute<ParamArrayAttribute>() != null
            };
        }

        private static string FormatDefaultValue(ParameterInfo parameter)
        {
            if (parameter.RawDefaultValue == null)
            {
                var parameterTypeInfo = parameter.ParameterType.GetTypeInfo();
                if (parameterTypeInfo.IsValueType)
                {
                    return $"default({TypeBaseline.GetTypeNameFor(parameterTypeInfo)})";
                }

                return "null";
            }

            if (parameter.ParameterType == typeof(string))
            {
                return $"\"{parameter.RawDefaultValue}\"";
            }

            if (parameter.ParameterType == typeof(char))
            {
                return $"'{parameter.RawDefaultValue}'";
            }

            if (parameter.ParameterType == typeof(bool) ||
                parameter.ParameterType == typeof(byte) ||
                parameter.ParameterType == typeof(sbyte) ||
                parameter.ParameterType == typeof(short) ||
                parameter.ParameterType == typeof(ushort) ||
                parameter.ParameterType == typeof(int) ||
                parameter.ParameterType == typeof(uint) ||
                parameter.ParameterType == typeof(long) ||
                parameter.ParameterType == typeof(ulong) ||
                parameter.ParameterType == typeof(double) ||
                parameter.ParameterType == typeof(float) ||
                parameter.ParameterType == typeof(decimal))
            {
                return parameter.RawDefaultValue.ToString();
            }

            throw new InvalidOperationException("Unsupported default value type");
        }
    }
}