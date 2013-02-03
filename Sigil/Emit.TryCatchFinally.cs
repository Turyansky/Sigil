﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Sigil.Impl;
using System.Reflection.Emit;

namespace Sigil
{
    public partial class Emit<DelegateType>
    {
        /// <summary>
        /// Start a new exception block.  This is roughly analogous to a `try` block in C#, but an exception block contains it's catch and finally blocks.
        /// </summary>
        public ExceptionBlock BeginExceptionBlock()
        {
            if (!Stack.IsRoot)
            {
                throw new SigilException("Stack should be empty when BeginExceptionBlock is called", Stack);
            }

            var labelDel = IL.BeginExceptionBlock();
            var label = new Label(this, labelDel, "__exceptionBlockEnd");

            var ret = new ExceptionBlock(label);

            TryBlocks[ret] = Tuple.Create(IL.Index, -1);

            CurrentExceptionBlock.Push(ret);

            return ret;
        }
        /// <summary>
        /// Ends the given exception block.
        /// 
        /// All catch and finally blocks associated with the given exception block must be ended before this method is called.
        /// </summary>
        public void EndExceptionBlock(ExceptionBlock forTry)
        {
            if (forTry == null)
            {
                throw new ArgumentNullException("forTry");
            }

            if (forTry.Owner != this)
            {
                throw new ArgumentException("forTry is not owned by this Emit, and thus cannot be used");
            }

            var location = TryBlocks[forTry];

            // Can't close the same exception block twice
            if (location.Item2 != -1)
            {
                throw new InvalidOperationException("ExceptionBlock has already been ended");
            }

            if (CurrentExceptionBlock.Count > 0 && forTry != CurrentExceptionBlock.Peek())
            {
                throw new InvalidOperationException("Cannot end outer ExceptionBlock " + forTry + " while inner EmitExceptionBlock " + CurrentExceptionBlock.Peek() + " is open");
            }

            // Can't close an exception block while there are outstanding catch blocks
            foreach (var kv in CatchBlocks)
            {
                if (kv.Key.ExceptionBlock != forTry) continue;

                if (kv.Value.Item2 == -1)
                {
                    throw new InvalidOperationException("Cannot end ExceptionBlock, CatchBlock " + kv.Key + " has not been ended");
                }
            }

            foreach (var kv in FinallyBlocks)
            {
                if (kv.Key.ExceptionBlock != forTry) continue;

                if (kv.Value.Item2 == -1)
                {
                    throw new InvalidOperationException("Cannot end ExceptionBlock, FinallyBlock " + kv.Key + " has not been ended");
                }
            }

            if (!CatchBlocks.Any(k => k.Key.ExceptionBlock == forTry) && !FinallyBlocks.Any(k => k.Key.ExceptionBlock == forTry))
            {
                throw new InvalidOperationException("Cannot end ExceptionBlock without defining at least one of a catch or finally block");
            }

            IL.EndExceptionBlock();

            TryBlocks[forTry] = Tuple.Create(location.Item1, IL.Index);

            Stack = new StackState();

            Marks[forTry.Label] = Tuple.Create(Stack, IL.Index);

            CurrentExceptionBlock.Pop();
        }

        /// <summary>
        /// Begins a catch block for the given exception type in the given exception block.
        /// 
        /// The given exception block must still be open.
        /// </summary>
        public CatchBlock BeginCatchBlock<ExceptionType>(ExceptionBlock forTry)
        {
            return BeginCatchBlock(forTry, typeof(ExceptionType));
        }

        /// <summary>
        /// Begins a catch block for all exceptions in the given exception block
        ///
        /// The given exception block must still be open.
        /// 
        /// Equivalent to BeginCatchBlock(typeof(Exception), forTry).
        /// </summary>
        public CatchBlock BeginCatchAllBlock(ExceptionBlock forTry)
        {
            return BeginCatchBlock<Exception>(forTry);
        }

        /// <summary>
        /// Begins a catch block for the given exception type in the given exception block.
        /// 
        /// The given exception block must still be open.
        /// </summary>
        public CatchBlock BeginCatchBlock(ExceptionBlock forTry, Type exceptionType)
        {
            if (exceptionType == null)
            {
                throw new ArgumentNullException("exceptionType");
            }

            if (forTry == null)
            {
                throw new ArgumentNullException("forTry");
            }

            if (forTry.Owner != this)
            {
                throw new ArgumentException(forTry + " is not owned by this Emit, and thus cannot be used");
            }

            if (CurrentExceptionBlock.Count > 0 && forTry != CurrentExceptionBlock.Peek())
            {
                throw new InvalidOperationException("Cannot start CatchBlock on " + forTry + " while inner ExceptionBlock is still open");
            }

            if (!typeof(Exception).IsAssignableFrom(exceptionType))
            {
                throw new ArgumentException("BeginCatchBlock expects a type descending from Exception, found " + exceptionType, "exceptionType");
            }

            var currentlyOpen = CatchBlocks.Where(c => c.Key.ExceptionBlock == forTry && c.Value.Item2 == -1).Select(s => s.Key).SingleOrDefault();
            if (currentlyOpen != null)
            {
                throw new InvalidOperationException("Cannot start a new catch block, " + currentlyOpen + " has not been ended");
            }

            if (!Stack.IsRoot)
            {
                throw new SigilException("Stack should be empty when BeginCatchBlock is called", Stack);
            }

            var tryBlock = TryBlocks[forTry];

            if (tryBlock.Item2 != -1)
            {
                throw new SigilException("BeginCatchBlock expects an unclosed exception block, but " + forTry + " is already closed", Stack);
            }

            IL.BeginCatchBlock(exceptionType);
            Stack = new StackState();
            Stack = Stack.Push(TypeOnStack.Get(exceptionType));

            var ret = new CatchBlock(this, exceptionType, forTry);

            CatchBlocks[ret] = Tuple.Create(IL.Index, -1);

            return ret;
        }

        /// <summary>
        /// Ends the given catch block.
        /// </summary>
        public void EndCatchBlock(CatchBlock forCatch)
        {
            if (forCatch == null)
            {
                throw new ArgumentNullException("forCatch");
            }

            if (forCatch.Owner != this)
            {
                throw new ArgumentException("forCatch is not owned by this Emit, and thus cannot be used");
            }

            if (!Stack.IsRoot)
            {
                throw new SigilException("Stack should be empty when EndCatchBlock is called", Stack);
            }

            var location = CatchBlocks[forCatch];

            if (location.Item2 != -1)
            {
                throw new InvalidOperationException("CatchBlock  has already been ended");
            }

            // There's no equivalent to EndCatchBlock in raw ILGenerator, so no call here.
            //   But that's kind of weird from a just-in-time validation standpoint.

            Sigil.Impl.BufferedILGenerator.UpdateOpCodeDelegate update;
            UpdateState(OpCodes.Leave, forCatch.ExceptionBlock.Label.LabelDel, out update);

            Branches[Stack.Unique()] = Tuple.Create(forCatch.ExceptionBlock.Label, IL.Index);

            BranchPatches[IL.Index] = Tuple.Create(forCatch.ExceptionBlock.Label, update, OpCodes.Leave);

            CatchBlocks[forCatch] = Tuple.Create(location.Item1, IL.Index);
        }

        /// <summary>
        /// Begins a finally block on the given exception block.
        /// 
        /// Only one finally block can be defined per exception block, and the block cannot appear within a catch block.
        /// 
        /// The given exception block must still be open.
        /// </summary>
        public FinallyBlock BeginFinallyBlock(ExceptionBlock forTry)
        {
            if (forTry == null)
            {
                throw new ArgumentNullException("forTry");
            }

            if (forTry.Owner != this)
            {
                throw new ArgumentException("forTry is not owned by this Emit, and thus cannot be used");
            }

            var tryBlock = TryBlocks[forTry];

            if (tryBlock.Item2 != -1)
            {
                throw new InvalidOperationException("BeginFinallyBlock expects an unclosed exception block, but " + forTry + " is already closed");
            }

            if (CurrentExceptionBlock.Count > 0 && forTry != CurrentExceptionBlock.Peek())
            {
                throw new InvalidOperationException("Cannot begin FinallyBlock on " + forTry + " while inner ExceptionBlock " + CurrentExceptionBlock.Peek() + " is still open");
            }

            if (FinallyBlocks.Any(kv => kv.Key.ExceptionBlock == forTry))
            {
                throw new InvalidOperationException("There can only be one finally block per ExceptionBlock, and one is already defined for " + forTry);
            }

            if (!Stack.IsRoot)
            {
                throw new SigilException("Stack should be empty when BeginFinallyBlock is called", Stack);
            }

            var ret = new FinallyBlock(this, forTry);

            IL.BeginFinallyBlock();

            FinallyBlocks[ret] = Tuple.Create(IL.Index, -1);

            return ret;
        }

        /// <summary>
        /// Ends the given finally block.
        /// </summary>
        public void EndFinallyBlock(FinallyBlock forFinally)
        {
            if (forFinally == null)
            {
                throw new ArgumentNullException("forFinally");
            }

            if (forFinally.Owner != this)
            {
                throw new ArgumentException("forFinally is not owned by this Emit, and thus cannot be used");
            }

            var finallyBlock = FinallyBlocks[forFinally];

            if (finallyBlock.Item2 != -1)
            {
                throw new InvalidOperationException("EndFinallyBlock expects an unclosed finally block, but " + forFinally + " is already closed");
            }

            if (!Stack.IsRoot)
            {
                throw new SigilException("Stack should be empty when EndFinallyBlock is called", Stack);
            }

            // There's no equivalent to EndFinallyBlock in raw ILGenerator, so no call here.
            //   But that's kind of weird from a just-in-time validation standpoint.

            UpdateState(OpCodes.Endfinally);

            FinallyBlocks[forFinally] = Tuple.Create(finallyBlock.Item1, IL.Index);
        }
    }
}
