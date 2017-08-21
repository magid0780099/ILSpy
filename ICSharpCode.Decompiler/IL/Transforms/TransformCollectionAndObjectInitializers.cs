﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.Decompiler.TypeSystem;

namespace ICSharpCode.Decompiler.IL.Transforms
{
	/// <summary>
	/// Transforms array initialization pattern of System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray.
	/// For collection and object initializers see <see cref="TransformInitializers"/>
	/// </summary>
	public class TransformCollectionAndObjectInitializers : IBlockTransform
	{
		BlockTransformContext context;

		void IBlockTransform.Run(Block block, BlockTransformContext context)
		{
			this.context = context;
			for (int i = block.Instructions.Count - 1; i >= 0; i--)
			{
				DoTransform(block, i);
			}
		}

		bool DoTransform(Block body, int pos)
		{
			ILInstruction inst = body.Instructions[pos];
			// Match stloc(v, newobj)
			if (inst.MatchStLoc(out var v, out var initInst)) {
				switch (initInst) {
					case NewObj newObjInst:
						if (DelegateConstruction.IsDelegateConstruction(newObjInst) || DelegateConstruction.IsPotentialClosure(context, newObjInst))
							return false;
						if (newObjInst.Method.DeclaringType.Kind != TypeKind.Struct && v.Kind != VariableKind.StackSlot)
							return false;
						break;
					case DefaultValue defaultVal:
						break;
					default:
						return false;
				}
				context.Step("CollectionOrObjectInitializer", inst);
				int initializerItemsCount = 0;
				var blockType = BlockType.CollectionInitializer;
				// Detect initializer type by scanning the following statements
				// each must be a callvirt with ldloc v as first argument
				// if the method is a setter we're dealing with an object initializer
				// if the method is named Add and has at least 2 arguments we're dealing with a collection/dictionary initializer
				while (pos + initializerItemsCount + 1 < body.Instructions.Count
					&& IsPartOfInitializer(body.Instructions, pos + initializerItemsCount + 1, v, ref blockType))
					initializerItemsCount++;
				if (initializerItemsCount == 0)
					return false;
				Block initBlock = new Block(blockType);
				var finalSlot = context.Function.RegisterVariable(VariableKind.StackSlot, v.Type);
				initBlock.FinalInstruction = new LdLoc(finalSlot);
				initBlock.Instructions.Add(new StLoc(finalSlot, initInst.Clone()));
				for (int i = 1; i <= initializerItemsCount; i++) {
					switch (body.Instructions[i + pos]) {
						case CallInstruction call:
							if (!(call is CallVirt || call is Call)) continue;
							var newCall = (CallInstruction)call.Clone();
							var newTarget = newCall.Arguments[0];
							foreach (var load in newTarget.Descendants.OfType<IInstructionWithVariableOperand>())
								if (load is LdLoc || load is LdLoca)
									load.Variable = finalSlot;
							initBlock.Instructions.Add(newCall);
							break;
						case StObj stObj:
							var newStObj = (StObj)stObj.Clone();
							foreach (var load in newStObj.Target.Descendants.OfType<IInstructionWithVariableOperand>())
								if (load is LdLoc || load is LdLoca)
									load.Variable = finalSlot;
							initBlock.Instructions.Add(newStObj);
							break;
					}

				}
				initInst.ReplaceWith(initBlock);
				for (int i = 0; i < initializerItemsCount; i++)
					body.Instructions.RemoveAt(pos + 1);
				ILInlining.InlineIfPossible(body, ref pos, context);
			}
			return true;
		}

		bool IsPartOfInitializer(InstructionCollection<ILInstruction> instructions, int pos, ILVariable target, ref BlockType blockType)
		{
			(var kind, var path, var values, var targetVariable) = AccessPathElement.GetAccessPath(instructions[pos]);
			switch (kind) {
				case AccessPathKind.Adder:
					return target == targetVariable;
				case AccessPathKind.Setter:
					if (values.Count == 1 && target == targetVariable) {
						blockType = BlockType.ObjectInitializer;
						return true;
					}
					return false;
				default:
					return false;
			}
		}
	}

	public enum AccessPathKind
	{
		Invalid,
		Setter,
		Adder
	}

	public struct AccessPathElement : IEquatable<AccessPathElement>
	{
		public AccessPathElement(IMember member, ILVariable index = null)
		{
			this.Member = member;
			this.Index = index;
		}

		public readonly IMember Member;
		public readonly ILVariable Index;

		public override string ToString() => $"[{Member}, {Index}]";

		public static (AccessPathKind Kind, List<AccessPathElement> Path, List<ILInstruction> Values, ILVariable Target) GetAccessPath(ILInstruction instruction)
		{
			List<AccessPathElement> path = new List<AccessPathElement>();
			ILVariable target = null;
			AccessPathKind kind = AccessPathKind.Invalid;
			List<ILInstruction> values = null;
			IMethod method;
			while (instruction != null) {
				switch (instruction) {
					case CallInstruction call:
						if (!(call is CallVirt || call is Call)) goto default;
						method = call.Method;
						if (!IsMethodApplicable(method)) goto default;
						instruction = call.Arguments[0];
						if (values == null) {
							values = new List<ILInstruction>(call.Arguments.Skip(1));
							if (values.Count == 0)
								goto default;
							if (method.IsAccessor) {
								kind = AccessPathKind.Setter;
							} else {
								kind = AccessPathKind.Adder;
							}
						}
						if (method.IsAccessor) {
							path.Insert(0, new AccessPathElement(method.AccessorOwner));
						} else {
							path.Insert(0, new AccessPathElement(method));
						}
						break;
					case LdObj ldobj:
						if (ldobj.Target is LdFlda ldflda) {
							path.Insert(0, new AccessPathElement(ldflda.Field));
							instruction = ldflda.Target;
							break;
						}
						goto default;
					case StObj stobj:
						if (stobj.Target is LdFlda ldflda2) {
							path.Insert(0, new AccessPathElement(ldflda2.Field));
							instruction = ldflda2.Target;
							if (values == null) {
								values = new List<ILInstruction>(new[] { stobj.Value });
								kind = AccessPathKind.Setter;
							}
							break;
						}
						goto default;
					case LdLoc ldloc:
						target = ldloc.Variable;
						instruction = null;
						break;
					case LdLoca ldloca:
						target = ldloca.Variable;
						instruction = null;
						break;
					default:
						kind = AccessPathKind.Invalid;
						instruction = null;
						break;
				}
			}
			if (kind != AccessPathKind.Invalid && values.SelectMany(v => v.Descendants).OfType<LdLoc>().Any(ld => ld.Variable == target))
				kind = AccessPathKind.Invalid;
			return (kind, path, values, target);
		}

		static bool IsMethodApplicable(IMethod method)
		{
			if (method.IsStatic)
				return false;
			if (method.IsAccessor)
				return true;
			return "Add".Equals(method.Name, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			if (obj is AccessPathElement)
				return Equals((AccessPathElement)obj);
			return false;
		}

		public override int GetHashCode()
		{
			int hashCode = 0;
			unchecked {
				if (Member != null)
					hashCode += 1000000007 * Member.GetHashCode();
				if (Index != null)
					hashCode += 1000000009 * Index.GetHashCode();
			}
			return hashCode;
		}

		public bool Equals(AccessPathElement other)
		{
			return other.Member.Equals(this.Member) && other.Index == this.Index;
		}

		public static bool operator ==(AccessPathElement lhs, AccessPathElement rhs)
		{
			return lhs.Equals(rhs);
		}

		public static bool operator !=(AccessPathElement lhs, AccessPathElement rhs)
		{
			return !(lhs == rhs);
		}
	}
}