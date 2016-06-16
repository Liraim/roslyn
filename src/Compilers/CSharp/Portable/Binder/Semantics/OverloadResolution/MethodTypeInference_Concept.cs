﻿using Microsoft.CodeAnalysis.CSharp.Symbols;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal sealed partial class MethodTypeInferrer
    {
        /// <summary>
        /// Performs the concept phase of type inference.
        /// <para>
        /// This phase occurs when the vanilla C# first and second phases have
        /// both failed.
        /// </para>
        /// <para>
        /// In this phase, we check to see whether the remaining unbound
        /// type parameters are concept witnesses.  If they are, then we
        /// find all currently visible implementations of the witnessed
        /// concept in scope, and check whether the set of implementations
        /// yields a viable type for the missing argument.
        /// </para>
        /// </summary>
        /// <param name="binder">
        /// The binder for the scope in which the type-inferred method
        /// resides.
        /// </param>
        /// <param name="useSiteDiagnostics">
        /// The diagnostics set for this use site.
        /// </param>
        /// <returns></returns>
        private bool InferTypeArgsConceptPhase(Binder binder, ref HashSet<DiagnosticInfo> useSiteDiagnostics)
        {
            // We shouldn't try this phase if we succeeded during the last one.
            Debug.Assert(!AllFixed());

            // First, make sure every unfixed type parameter is a concept, and
            // that we know where they all are so we can infer them later.
            ImmutableArray<int> conceptIndices;
            if (!GetMethodUnfixedConceptWitnesses(out conceptIndices)) return false;

            // If we got this far, we should have at least something to infer.
            Debug.Assert(!conceptIndices.IsEmpty);

            // We'll be checking to see if concepts defined on the missing
            // witness type parameters are implemented.  Since this means we
            // are checking something on the method definition, but need it
            // in terms of our fixed type arguments, we must make a mapping
            // from parameters to arguments.
            var fixedMap = this.MakeMethodFixedMap();

            // We need two things from the outer scope:
            // 1) All instances visible to this method call;
            // 2) All type parameters bound in the method and class.
            // For efficiency, we do these in one go.
            // TODO: Ideally this should be cached at some point, perhaps on the
            // compilation or binder.
            ImmutableArray<TypeSymbol> allInstances;
            ImmutableHashSet<TypeParameterSymbol> boundParams;
            SearchScopeForInstancesAndParams(binder, out allInstances, out boundParams);

            var inferrer = new ConceptWitnessInferrer(allInstances, boundParams);
            bool success = true;
            foreach (int j in conceptIndices)
            {
                var maybeFixed = inferrer.Infer(_methodTypeParameters[j], fixedMap, ImmutableHashSet<NamedTypeSymbol>.Empty);
                if (maybeFixed == null) return false;
                Debug.Assert(maybeFixed != null && maybeFixed.IsInstanceType());
                _fixedResults[j] = maybeFixed;
            }

            return success;
        }

        #region Setup

        /// <summary>
        /// Checks that every unfixed type parameter is a concept witness, and
        /// stores their indices into an array.
        /// </summary>
        /// <param name="indices">
        /// The outgoing array of unfixed concept witnesses.
        /// </param>
        /// <returns>
        /// True if, and only if, every unfixed type parameter is a concept
        /// witness.
        /// </returns>
        private bool GetMethodUnfixedConceptWitnesses(out ImmutableArray<int> indices)
        {
            var iBuilder = new ArrayBuilder<int>();

            for (int i = 0; i < _methodTypeParameters.Length; i++)
            {
                if (IsUnfixed(i))
                {
                    if (!_methodTypeParameters[i].IsConceptWitness)
                    {
                        iBuilder.Free();
                        return false;
                    }
                    iBuilder.Add(i);
                }
            }

            indices = iBuilder.ToImmutableAndFree();
            return true;
        }

        /// <summary>
        /// Constructs a map from fixed method type parameters to their
        /// inferred arguments.
        /// </summary>
        /// <returns>
        /// A map mapping each fixed parameter to its argument.
        /// </returns>
        private MutableTypeMap MakeMethodFixedMap()
        {
            MutableTypeMap mt = new MutableTypeMap();

            for (int i = 0; i < _methodTypeParameters.Length; i++)
            {
                if (_fixedResults[i] != null)
                {
                    mt.Add(_methodTypeParameters[i], new TypeWithModifiers(_fixedResults[i]));
                }
            }

            return mt;
        }

        /// <summary>
        /// Traverses the scope induced by the given binder for visible
        /// instances and fixed type parameters
        /// </summary>
        /// <param name="binder">
        /// The binder providing scope for this query.
        /// </param>
        /// <param name="allInstances">
        /// An immutable array of symbols (either type parameters or named
        /// types) representing concept instances available in the scope
        /// of <paramref name="binder"/>.
        /// </param>
        /// <param name="fixedParams">
        /// An immutable array of symbols (either type parameters or named
        /// types) representing concept instances available in the scope
        /// of <paramref name="binder"/>.
        /// </param>
        private static void SearchScopeForInstancesAndParams(Binder binder,
            out ImmutableArray<TypeSymbol> allInstances,
            out ImmutableHashSet<TypeParameterSymbol> fixedParams)
        {
            var iBuilder = new ArrayBuilder<TypeSymbol>();
            var fpBuilder = new HashSet<TypeParameterSymbol>();

            // This is used to make sure we don't traverse the same container twice.
            Symbol oldContainer = null;
            for (var b = binder; b != null; b = b.Next)
            {
                // ContainingMember crashes if we're in a BuckStopsHereBinder.
                var container = b.ContainingMemberOrLambda;
                if (container == null) break;
                if (container == oldContainer) continue;
                oldContainer = container;

                // We can see two types of instance:
                // 1) Any instances witnessed on a method or type between us and
                //    the global namespace (this is also where we can find
                //    fixed type parameters);
                SearchContainerForInstancesAndParams(container, ref iBuilder, ref fpBuilder);
                // 2) Any visible named instance.  (See below, too).
                GetNamedInstances(binder, container, ref iBuilder);

                // The above is ok if we just want to get all instances in
                // a straight line up the scope from here to the global
                // namespace, but we also need to pull in imports too.
                foreach (var u in b.GetImports(null).Usings)
                {
                    // TODO: Do we need to recurse into nested types/namespaces?
                    GetNamedInstances(binder, u.NamespaceOrType, ref iBuilder);
                }
            }

            allInstances = iBuilder.ToImmutableAndFree();
            fixedParams = fpBuilder.ToImmutableHashSet();
        }

        /// <summary>
        /// Adds all constraint witnesses in a parent member or type to an array.
        /// </summary>
        /// <param name="container">
        /// The container symbol to query.
        /// </param>
        /// <param name="instances">
        /// The instance array to populate with witnesses.
        /// </param>
        /// <param name="fixedParams">
        /// The set to populate with fixed type parameters.
        /// </param>
        private static void SearchContainerForInstancesAndParams(Symbol container,
            ref ArrayBuilder<TypeSymbol> instances,
            ref HashSet<TypeParameterSymbol> fixedParams)
        {
            // Only methods and named types have constrained witnesses.
            if (container.Kind != SymbolKind.Method && container.Kind != SymbolKind.NamedType) return;

            ImmutableArray<TypeParameterSymbol> tps = ConceptWitnessInferrer.GetTypeParametersOf(container);

            foreach (var tp in tps)
            {
                if (tp.IsConceptWitness) instances.Add(tp);
                fixedParams.Add(tp);
            }
        }

        /// <summary>
        /// Adds all named-type instances inside a container and visible in this scope to an array.
        /// </summary>
        /// <param name="binder">
        /// The binder providing scope for this query.
        /// </param>
        /// <param name="container">
        /// The current container being searched for instanes.
        /// </param>
        /// <param name="instances">
        /// The instance array to populate with witnesses.
        /// </param>
        private static void GetNamedInstances(Binder binder, Symbol container, ref ArrayBuilder<TypeSymbol> instances)
        {
            var ignore = new HashSet<DiagnosticInfo>();

            // Only namespaces and named kinds have named instances.
            if (container.Kind != SymbolKind.Namespace && container.Kind != SymbolKind.NamedType) return;

            foreach (var member in ((NamespaceOrTypeSymbol)container).GetTypeMembers())
            {
                if (!binder.IsAccessible(member, ref ignore, binder.ContainingType)) continue;

                // Assuming that instances don't contain sub-instances.
                if (member.IsInstance)
                {
                    instances.Add(member);
                }
            }
        }

        #endregion Setup
    }

    /// <summary>
    /// An object that, given a series of viable instances and bound type
    /// parameters, can perform concept witness inference.
    /// </summary>
    internal class ConceptWitnessInferrer
    {
        /// <summary>
        /// The list of all instances in scope for this inferrer.
        /// These can be either type parameters (eg. witnesses passed in
        /// through constraints at the method or class level) or named
        /// types (instance declarations).
        /// </summary>
        private ImmutableArray<TypeSymbol> _allInstances;

        /// <summary>
        /// The set of all type parameters in scope that are bound:
        /// we cannot substitute for them in unification.  Usually this is
        /// the set of type parameters introduced through type parameter
        /// lists on methods and classes in scope.
        /// </summary>
        private ImmutableHashSet<TypeParameterSymbol> _boundParams;

        /// <summary>
        /// Constructs a new ConceptWitnessInferrer.
        /// </summary>
        /// <param name="allInstances">
        /// The list of all instances in scope for this inferrer.
        /// </param>
        /// <param name="boundParams">
        /// The set of all type parameters in scope that are bound, and
        /// cannot be substituted out in unification.
        /// </param>
        public ConceptWitnessInferrer(ImmutableArray<TypeSymbol> allInstances,
            ImmutableHashSet<TypeParameterSymbol> boundParams)
        {
            _allInstances = allInstances;
            _boundParams = boundParams;
        }

        #region Main driver

        /// <summary>
        /// Tries to infer the concept witness for the given type parameter.
        /// </summary>
        /// <param name="typeParam">
        /// The type parameter that is the concept witness to infer.
        /// </param>
        /// <param name="fixedMap">
        /// The map from all of the fixed, non-witness type parameters in the
        /// same type parameter list as <paramref name="typeParam"/>
        /// to their arguments.
        /// </param>
        /// <param name="chain">
        /// The set of instances we've passed through recursively to get here,
        /// used to abort recursive calls if they will create cycles.
        /// </param>
        /// <returns>
        /// Null if inference failed; else, the inferred concept instance.
        /// </returns>
        internal TypeSymbol Infer(TypeParameterSymbol typeParam,
            MutableTypeMap fixedMap,
            ImmutableHashSet<NamedTypeSymbol> chain)
        {
            // @t-mawind
            // An instance satisfies inference if:
            //
            // 1) for all concepts required by the type parameter, at least
            //    one concept implemented by the instances unifies with that
            //    concept without capturing bound type parameters;
            // 2) all of the type parameters of that instance can be bound,
            //    both by the substitutions from the unification above and also
            //    by recursively trying to infer any missing concept witnesses.
            //
            // The first part is equivalent to establishing
            //    witness :- instance.
            //
            // The second part is equivalent to resolving
            //    instance :- dependency1; dependency2; ...
            // by trying to establish the dependencies as separate queries.
            //
            // After the second part, if we have multiple possible instances,
            // we try to see if one implements a subconcept of all of the other
            // instances.  If so, we narrow to that specific instance.
            //
            // If we have multiple satisfying instances, or zero, we fail.

            // TODO: We don't yet have #2, so we presume that if we have any
            // concept-witness type parameters we've failed.

            var requiredConcepts = GetRequiredConceptsFor(typeParam, fixedMap);
            var firstPassInstances = AllInstancesSatisfyingGoal(requiredConcepts);

            // We can't infer if none of the instances implement our concept!
            // However, if we have more than one candidate instance at this
            // point, we shouldn't bail until we've made sure only one of them
            // passes 2).
            if (firstPassInstances.IsEmpty) return null;

            var secondPassInstances = ToSatisfiableInstances(firstPassInstances, chain);

            // We only do the third pass if the second pass returned too many items.
            var thirdPassInstances = secondPassInstances;
            if (secondPassInstances.Length > 1) thirdPassInstances = TieBreakInstances(secondPassInstances);

            // Either ambiguity, or an outright lack of inference success.
            if (thirdPassInstances.Length != 1) return null;
            return thirdPassInstances[0];
        }

        /// <summary>
        /// Deduces the set of concepts that must be implemented by any witness
        /// supplied to the given type parameter.
        /// </summary>
        /// <param name="typeParam">
        /// The type parameter being inferred.
        /// </param>
        /// <param name="fixedMap">
        /// A map mapping fixed type parameters to their type arguments.
        /// </param>
        /// <returns>
        /// An array of concepts required by <paramref name="typeParam"/>.
        /// </returns>
        private static ImmutableArray<TypeSymbol> GetRequiredConceptsFor(TypeParameterSymbol typeParam, MutableTypeMap fixedMap)
        {
            var rawRequiredConcepts = typeParam.AllEffectiveInterfacesNoUseSiteDiagnostics;

            // The concepts from above are in terms of the method's type
            // parameters.  In order to be able to unify properly, we need to
            // substitute the inferences we've made so far.
            var rc = new ArrayBuilder<TypeSymbol>();
            foreach (var con in rawRequiredConcepts)
            {
                rc.Add(fixedMap.SubstituteType(con).AsTypeSymbolOnly());
            }

            var unused = new HashSet<DiagnosticInfo>();

            // Now we can do some optimisation: if we're asking for a concept,
            // we don't need to ask for its base concepts.
            var rc2 = new ArrayBuilder<TypeSymbol>();
            foreach (var c1 in rc)
            {
                var needed = true;
                foreach (var c2 in rc)
                {
                    if (c2.ImplementsInterface(c1, ref unused))
                    {
                        needed = false;
                        break;
                    }
                }
                if (needed) rc2.Add(c1);
            }

            rc.Free();
            return rc2.ToImmutableAndFree();
        }

        /// <summary>
        /// Gets the type parameters of an arbitrary symbol.
        /// </summary>
        /// <param name="symbol">
        /// The symbol for which we are getting type parameters.
        /// </param>
        /// <returns>
        /// If the symbol is a generic method or named type, its parameters;
        /// else, the empty list.
        /// </returns>
        internal static ImmutableArray<TypeParameterSymbol> GetTypeParametersOf(Symbol symbol)
        {
            switch (symbol.Kind)
            {
                case SymbolKind.Method:
                    return ((MethodSymbol)symbol).TypeParameters;
                case SymbolKind.NamedType:
                    return ((NamedTypeSymbol)symbol).TypeParameters;
                default:
                    return ImmutableArray<TypeParameterSymbol>.Empty;
            }
        }

        #endregion Main driver
        #region First pass

        /// <summary>
        /// Performs the first pass of concept witness type inference.
        /// <para>
        /// This pass filters down a list of all possible instances into a set
        /// of candidate instances, such that each candidate instance
        /// implements all of the concepts required by the parameter being
        /// inferred.
        /// </para>
        /// </summary>
        /// <param name="requiredConcepts">
        /// The list of concepts required by the type parameter being inferred.
        /// </param>
        /// <returns>
        /// An array of candidate instances after the first pass.
        /// </returns>
        private ImmutableArray<TypeSymbol> AllInstancesSatisfyingGoal(ImmutableArray<TypeSymbol> requiredConcepts)
        {
            // First, collect all of the instances satisfying 1).
            var firstPassInstanceBuilder = new ArrayBuilder<TypeSymbol>();
            foreach (var instance in _allInstances)
            {
                MutableTypeMap unifyingSubstitutions;
                if (AllRequiredConceptsProvided(requiredConcepts, instance, out unifyingSubstitutions))
                {
                    // The unification may have provided us with substitutions
                    // that were needed to make the provided concepts fit the
                    // required concepts.
                    //
                    // It may be that some of these substitutions also need to
                    // apply to the actual instance so it can satisfy #2.
                    var result = unifyingSubstitutions.SubstituteType(instance).AsTypeSymbolOnly();
                    firstPassInstanceBuilder.Add(result);
                }
            }
            return firstPassInstanceBuilder.ToImmutableAndFree();
        }

        /// <summary>
        /// Checks whether a list of required concepts is implemented by a
        /// candidate instance modulo unifying substitutions.
        /// <para>
        /// We don't check yet that the instance itself is satisfiable, just that
        /// it will satisfy our concept list if it is.
        /// </para>
        /// </summary>
        /// <param name="requiredConcepts">
        /// The list of required concepts to implement.
        /// </param>
        /// <param name="instance">
        /// The candidate instance.
        /// </param>
        /// <param name="unifyingSubstitutions">
        /// A map of type substitutions, populated by this method, which are
        /// required in order to make the instance implement the concepts.
        /// </param>
        /// <returns>
        /// True if, and only if, the given instance implements the given list
        /// of concepts.
        /// </returns>
        private bool AllRequiredConceptsProvided(ImmutableArray<TypeSymbol> requiredConcepts,
                                                 TypeSymbol instance,
                                                 out MutableTypeMap unifyingSubstitutions)
        {
            unifyingSubstitutions = new MutableTypeMap();

            var providedConcepts =
                ((instance as TypeParameterSymbol)?.AllEffectiveInterfacesNoUseSiteDiagnostics
                 ?? ((instance as NamedTypeSymbol)?.AllInterfacesNoUseSiteDiagnostics)
                 ?? ImmutableArray<NamedTypeSymbol>.Empty);

            foreach (var requiredConcept in requiredConcepts)
            {
                bool thisProvided = false;
                foreach (var providedConcept in providedConcepts)
                {
                    if (TypeUnification.CanUnify(providedConcept,
                            requiredConcept,
                            ref unifyingSubstitutions,
                            _boundParams))
                    {
                        thisProvided = true;
                        break;
                    }
                }

                if (!thisProvided) return false;
            }

            // If we got here, all required concepts must have been provided.
            return true;
        }

        #endregion First pass
        #region Second pass

        /// <summary>
        /// Performs the second pass of concept witness type inference.
        /// <para>
        /// This pass tries to fix any witness parameters in each candidate
        /// instance, eliminating it if it either has unfixed non-witness
        /// parameters, or the witness parameters cannot be fixed.  To do this,
        /// it recursively begins inference on the missing witnesses.
        /// </para>
        /// </summary>
        /// <param name="candidateInstances">
        /// The set of candidate instances after the first pass.
        /// </param>
        /// <param name="chain">
        /// The set of instances we've passed through recursively to get here,
        /// used to abort recursive calls if they will create cycles.
        /// </param>
        /// <returns>
        /// An array of candidate instances after the first pass.
        /// </returns>
        private ImmutableArray<TypeSymbol> ToSatisfiableInstances(ImmutableArray<TypeSymbol> candidateInstances,
            ImmutableHashSet<NamedTypeSymbol> chain)
        {
            var secondPassInstanceBuilder = new ArrayBuilder<TypeSymbol>();
            foreach (var instance in candidateInstances)
            {
                // Assumption: no witness parameter can depend on any other
                // witness parameter, so we can do recursive inference in
                // one pass.
                ImmutableHashSet<TypeParameterSymbol> unfixedWitnesses;
                if (!GetRecursiveUnfixedConceptWitnesses(instance, out unfixedWitnesses))
                {
                    // This instance has some unfixed non-witness type
                    // parameters.  We can't infer these, so give up on this
                    // candidate instance.
                    continue;
                }

                var fixedInstance = instance;
                // If there were no unfixed witnesses, we don't need to bother
                // with recursive inference--there's nothing to infer!
                if (!unfixedWitnesses.IsEmpty)
                {
                    // Type parameters can't have unfixed witnesses.
                    Debug.Assert(instance.Kind == SymbolKind.NamedType);
                    var nt = (NamedTypeSymbol)instance;

                    // Do cycle detection: have we already set up a recursive
                    // call for this instance with these type parameters?
                    if (chain.Contains(nt)) continue;
                    var newChain = chain.Add(nt);

                    // If this call fails, we couldn't infer all of the
                    // witnesses.  By our assumption, we can't infer anything
                    // more on this instance, so we give up on it.
                    MutableTypeMap recurSubstMap;
                    if (!InferRecursively(nt, unfixedWitnesses, newChain, out recurSubstMap)) continue;

                    // Else, we now have a map that should fix all of the
                    // remaining parameters.
                    fixedInstance = recurSubstMap.SubstituteType(instance).AsTypeSymbolOnly();
                }

                // If we got this far, the instance _should_ have no unfixed
                // parameters, and can now be considered as a candidate for
                // inference.
                secondPassInstanceBuilder.Add(fixedInstance);
            }
            return secondPassInstanceBuilder.ToImmutableAndFree();
        }

        /// <summary>
        /// Prepares a recursive inference round on an instance.
        /// </summary>
        /// <param name="instance">
        /// The instance whose missing witnesses are to be inferred.
        /// </param>
        /// <param name="unfixedWitnesses">
        /// The set of unfixed witness parameters to infer.
        /// </param>
        /// <param name="chain">
        /// The set of instances we've passed through recursively to get here,
        /// used to abort the recursive call if it will create a cycle.
        /// </param>
        /// <param name="recurSubstMap">
        /// The map of witness-fixing substitutions to return on success.
        /// </param>
        /// <returns>
        /// True if, and only if, we were able to infer every unfixed witness
        /// for this instance without generating cycles.
        /// </returns>
        private bool InferRecursively(NamedTypeSymbol instance,
            ImmutableHashSet<TypeParameterSymbol> unfixedWitnesses,
            ImmutableHashSet<NamedTypeSymbol> chain,
            out MutableTypeMap recurSubstMap)
        {
            // This ensures cycle detection will work.
            Debug.Assert(chain.Contains(instance));

            // In recursive inference, the set of known type argument
            // substitutions is those we made when fixing this instance.
            // We thus need to re-make the fixedMap.
            var recurFixedMap = new MutableTypeMap();
            var targs = instance.TypeArguments;
            var tpars = instance.TypeParameters;
            for (int i = 0; i < tpars.Length; i++)
            {
                if (tpars[i] != targs[i]) recurFixedMap.Add(tpars[i], new TypeWithModifiers(targs[i]));
            }

            // Now try to infer the unfixed witnesses, recursively.
            // TODO: can this be flattened into an iterative process?
            // It shouldn't be a massive performance or stack issue,
            // but still...
            recurSubstMap = new MutableTypeMap();
            foreach (var unfixed in unfixedWitnesses)
            {
                var maybeFixed = Infer(unfixed, recurFixedMap, chain);
                if (maybeFixed == null) return false;
                recurSubstMap.Add(unfixed, new TypeWithModifiers(maybeFixed));
            }

            return true;
        }

        /// <summary>
        /// Tries to find all unfixed type parameters in a candidate instance,
        /// adds those which are witnesses to a list, and fails if any is not
        /// a witness.
        /// </summary>
        /// <param name="instance">
        /// The candidate instance to investigate.
        /// </param>
        /// <param name="unfixed">
        /// The list of unfixed witness parameters to populate.
        /// </param>
        /// <returns>
        /// True if we didn't see any unfixed non-witness type parameters,
        /// which is a blocker on accepting <paramref name="instance"/> as a
        /// witness; false otherwise.
        /// </returns>
        private static bool GetRecursiveUnfixedConceptWitnesses(TypeSymbol instance, out ImmutableHashSet<TypeParameterSymbol> unfixed)
        {
            Debug.Assert(instance.Kind == SymbolKind.NamedType || instance.Kind == SymbolKind.TypeParameter);

            var uBuilder = new ArrayBuilder<TypeParameterSymbol>();

            // Only named types (ie instance declarations) can contain
            // unresolved concept witnesses.
            if (instance.Kind != SymbolKind.NamedType)
            {
                unfixed = uBuilder.ToImmutableHashSet();
                uBuilder.Free();
                return true;
            }

            var nt = (NamedTypeSymbol)instance;
            var targs = nt.TypeArguments;
            var tpars = nt.TypeParameters;
            for (int i = 0; i < tpars.Length; i++)
            {
                // If a type parameter is its own argument, we assume this
                // means it hasn't yet been fixed.
                if (tpars[i] == targs[i])
                {
                    if (tpars[i].IsConceptWitness)
                    {
                        uBuilder.Add(tpars[i]);
                    }
                    else
                    {
                        // This is an unfixed non-witness, which kills off our
                        // attempt to use this instance completely.
                        unfixed = ImmutableHashSet<TypeParameterSymbol>.Empty;
                        uBuilder.Free();
                        return false;
                    }
                }
            }

            // If we got here, then we haven't seen any unfixed non-witnesses.
            unfixed = uBuilder.ToImmutableHashSet();
            uBuilder.Free();
            return true;
        }

        #endregion Second pass
        #region Third pass

        /// <summary>
        /// Performs the third pass of concept witness type inference.
        /// <para>
        /// This pass tries to find a single instance in the candidate set that
        /// is 'better' than all other instances, eg. its concept is a strict
        /// super-interface of all other candidate instances' concepts.
        /// </para>
        /// </summary>
        /// <param name="candidateInstances">
        /// The set of instances to narrow.
        /// </param>
        /// <returns>
        /// An array of candidate instances after the third pass.
        /// </returns>
        private static ImmutableArray<TypeSymbol> TieBreakInstances(ImmutableArray<TypeSymbol> candidateInstances)
        {
            // This pass is useless if we have zero or one witnesses.
            Debug.Assert(1 < candidateInstances.Length);

            // We now perform an array of 'better concept witness' checks to
            // try to narrow the list of instances to zero or one.

            var mostSpecificConceptInstances = FilterToMostSpecificConceptInstances(candidateInstances);
            Debug.Assert(mostSpecificConceptInstances.Length <= candidateInstances.Length);
            if (mostSpecificConceptInstances.Length <= 1) return mostSpecificConceptInstances;

            var mostSpecificParamInstances = FilterToMostSpecificParamInstances(mostSpecificConceptInstances);
            Debug.Assert(mostSpecificParamInstances.Length <= mostSpecificConceptInstances.Length);

            return mostSpecificParamInstances;
        }

        /// <summary>
        /// Filters a set of candidate instances to those that implement at
        /// least all of the concepts of every other candidate instance.
        /// </summary>
        /// <param name="candidateInstances">
        /// The set of instances to filter.
        /// </param>
        /// <returns>
        /// <paramref name="candidateInstances"/>, filtered to contain only
        /// those instances that implement every concept of every other
        /// instance in <paramref name="candidateInstances"/>.
        /// </returns>
        private static ImmutableArray<TypeSymbol> FilterToMostSpecificConceptInstances(ImmutableArray<TypeSymbol> candidateInstances)
        {
            Debug.Assert(1 < candidateInstances.Length);

            var arb = new ArrayBuilder<TypeSymbol>();

            // TODO: This can invariably be made more efficient.
            foreach (var instance in candidateInstances)
            {
                if (ImplementsConceptsOfOtherInstances(instance, candidateInstances)) arb.Add(instance);
                // Note that this will only break ties if one instance
                // implements effectively sub-concepts of all other
                // instances: if two instances implement precisely the
                // same concept set, both will be added to arb and the
                // check will fail.
            }

            return arb.ToImmutableAndFree();
        }

        /// <summary>
        /// Checks whether one instance implements all of the concepts, either
        /// directly or through sub-concepts, of a set of other concepts.
        /// </summary>
        /// <param name="instance">
        /// The instance to compare.
        /// </param>
        /// <param name="otherInstances">
        /// A list of other instances to which this instance should be compared.
        /// This may include <paramref name="instance"/>, in which case it will
        /// be ignored.
        /// </param>
        /// <returns>
        /// True if, and only if, <paramref name="instance"/> implements all of
        /// the concepts of instances in <paramref name="otherInstances"/>.
        /// </returns>
        private static bool ImplementsConceptsOfOtherInstances(TypeSymbol instance, ImmutableArray<TypeSymbol> otherInstances)
        {
            Debug.Assert(!otherInstances.IsEmpty);

            var ignore = new HashSet<DiagnosticInfo>();

            foreach (var otherInstance in otherInstances)
            {
                if (otherInstance == instance) continue;

                foreach (var iface in otherInstance.AllInterfacesNoUseSiteDiagnostics)
                {
                    if (!iface.IsConcept) continue;
                    if (!instance.ImplementsInterface(iface, ref ignore)) return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Filters a set of candidate instances to those whose type parameters
        /// are no less specific than those of any other instance.
        /// <para>
        /// If any instance is more specific than the others, then the others
        /// are less specific and removed, returning the one 'best' instance.
        /// </para>
        /// <para>
        /// Currently, we only rule that one instance is more specific than the
        /// other if it has non-witness type parameters whereas the other does
        /// not.  This is probably overly conservative, however.
        /// </para>
        /// </summary>
        /// <param name="candidateInstances">
        /// The set of instances to filter.
        /// </param>
        /// <returns>
        /// <paramref name="candidateInstances"/>, filtered to contain only
        /// those instances whose type parameters are more specific than those
        /// of any other instance in <paramref name="candidateInstances"/>.
        /// </returns>
        private static ImmutableArray<TypeSymbol> FilterToMostSpecificParamInstances(ImmutableArray<TypeSymbol> candidateInstances)
        {
            Debug.Assert(1 < candidateInstances.Length);

            var arb = new ArrayBuilder<TypeSymbol>();

            // TODO: This can invariably be made more efficient.
            foreach (var instance in candidateInstances)
            {
                if (!ParamsLessSpecific(instance, candidateInstances)) arb.Add(instance);
            }

            return arb.ToImmutableAndFree();
        }

        /// <summary>
        /// Decides whether an instance is strictly less specific than at least
        /// one other instance.
        /// </summary>
        /// <param name="instance">
        /// The instance to compare.
        /// </param>
        /// <param name="otherInstances">
        /// A list of other instances to which this instance should be compared.
        /// This may include <paramref name="instance"/>, in which case it will
        /// be ignored.
        /// </param>
        /// <returns>
        /// True if, and only if, <paramref name="instance"/> is strictly less
        /// specific than any of the other instances in
        /// <paramref name="otherInstances"/>.
        /// </returns>
        private static bool ParamsLessSpecific(TypeSymbol instance, ImmutableArray<TypeSymbol> otherInstances)
        {
            // Currently, we do a very basic check based on non-witness type
            // parameter counts.  This could be much more sophisticated.

            bool instanceHasNonWitnesses = false;
            foreach (var typeParam in GetTypeParametersOf(instance))
            {
                if (!typeParam.IsConceptWitness)
                {
                    instanceHasNonWitnesses = true;
                    break;
                }
            }

            // No need to do the below check if we don't have non-witness type
            // params: the only way something can be more specific than us at
            // the moment is if we weren't.
            // This will need to go if we do something more sophisticated.
            if (!instanceHasNonWitnesses) return false;

            foreach (var otherInstance in otherInstances)
            {
                if (instance == otherInstance) continue;

                // TODO: cache this per instance?
                bool otherHasNonWitnesses = false;
                foreach (var typeParam in GetTypeParametersOf(otherInstance))
                {
                    if (!typeParam.IsConceptWitness)
                    {
                        otherHasNonWitnesses = true;
                        break;
                    }
                }

                // An instance is more specific if it has no non-witness type
                // parameters, but the other instance does.  Flip this logic to
                // get an early less-specific result.
                if (instanceHasNonWitnesses && !otherHasNonWitnesses) return true;
            }

            return false;
        }

        #endregion Third pass
    }
}