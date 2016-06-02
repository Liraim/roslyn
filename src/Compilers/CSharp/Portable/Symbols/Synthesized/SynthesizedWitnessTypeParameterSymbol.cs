﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

// @t-mawind TODO: reorganise/rename this

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Roslyn.Utilities;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;

namespace Microsoft.CodeAnalysis.CSharp.Symbols
{
    /// <summary>
    /// Base type of synthesised concept-witness type parameters.
    /// <para>
    /// These symbols appear whenever we find a constraint <c>A: B</c> where
    /// <c>B</c> is a concept and <c>A</c> is not in the formal type parameters
    /// of the parent symbol.
    /// </para>
    /// </summary>
    internal abstract class SynthesizedWitnessParameterSymbolBase : TypeParameterSymbol
    {
        //@t-mawind
        // This class is mainly based on (copies code from!) both
        // SourceTypeParameterSymbolBase and AnonymousTypeParameterSymbol.

        private int _ordinal;
        private Location _clauseLocation;
        private TypeParameterBounds _lazyBounds = TypeParameterBounds.Unset;
        private SymbolCompletionState _state;
        private string _name;

        /// <summary>
        /// Constructs a new SynthesizedWitnessParameterSymbolBase.
        /// </summary>
        /// <param name="name">
        /// The name of the type parameter.
        /// </param>
        /// <param name="clauseLocation">
        /// The location of the clause creating this witness.
        /// </param>
        /// <param name="ordinal">
        /// The ordinal of the type parameter.
        /// </param>
        protected SynthesizedWitnessParameterSymbolBase(string name, Location clauseLocation, int ordinal)
        {
            _name = name;
            _ordinal = (short)ordinal;
            _clauseLocation = clauseLocation;
        }

        public override ImmutableArray<Location> Locations => ImmutableArray<Location>.Empty;

        public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => ImmutableArray<SyntaxReference>.Empty;

        public override int Ordinal => _ordinal;

        public override VarianceKind Variance => VarianceKind.None; // TODO: check?

        public override string Name => _name;

        internal override ImmutableArray<TypeSymbol> GetConstraintTypes(ConsList<TypeParameterSymbol> inProgress)
        {
            var bounds = this.GetBounds(inProgress);
            return (bounds != null) ? bounds.ConstraintTypes : ImmutableArray<TypeSymbol>.Empty;
        }

        internal override ImmutableArray<NamedTypeSymbol> GetInterfaces(ConsList<TypeParameterSymbol> inProgress)
        {
            var bounds = this.GetBounds(inProgress);
            return (bounds != null) ? bounds.Interfaces : ImmutableArray<NamedTypeSymbol>.Empty;
        }

        internal override NamedTypeSymbol GetEffectiveBaseClass(ConsList<TypeParameterSymbol> inProgress)
        {
            var bounds = this.GetBounds(inProgress);
            return (bounds != null) ? bounds.EffectiveBaseClass : this.GetDefaultBaseType();
        }

        internal override TypeSymbol GetDeducedBaseType(ConsList<TypeParameterSymbol> inProgress)
        {
            var bounds = this.GetBounds(inProgress);
            return (bounds != null) ? bounds.DeducedBaseType : this.GetDefaultBaseType();
        }

        internal override void EnsureAllConstraintsAreResolved()
        {
            if (ReferenceEquals(_lazyBounds, TypeParameterBounds.Unset))
            {
                EnsureAllConstraintsAreResolved(this.ContainerTypeParameters);
            }
        }

        protected abstract ImmutableArray<TypeParameterSymbol> ContainerTypeParameters
        {
            get;
        }

        private TypeParameterBounds GetBounds(ConsList<TypeParameterSymbol> inProgress)
        {
            Debug.Assert(!inProgress.ContainsReference(this));
            Debug.Assert(!inProgress.Any() || ReferenceEquals(inProgress.Head.ContainingSymbol, this.ContainingSymbol));

            if (ReferenceEquals(_lazyBounds, TypeParameterBounds.Unset))
            {
                var diagnostics = DiagnosticBag.GetInstance();
                var bounds = this.ResolveBounds(inProgress, diagnostics);

                if (ReferenceEquals(Interlocked.CompareExchange(ref _lazyBounds, bounds, TypeParameterBounds.Unset), TypeParameterBounds.Unset))
                {
                    this.CheckConstraintTypeConstraints(diagnostics);
                    this.AddDeclarationDiagnostics(diagnostics);
                    _state.NotePartComplete(CompletionPart.TypeParameterConstraints);
                }

                diagnostics.Free();
            }
            return _lazyBounds;
        }

        protected abstract TypeParameterBounds ResolveBounds(ConsList<TypeParameterSymbol> inProgress, DiagnosticBag diagnostics);

        /// <summary>
        /// Check constraints of generic types referenced in constraint types. For instance,
        /// with "interface I&lt;T&gt; where T : I&lt;T&gt; {}", check T satisfies constraints
        /// on I&lt;T&gt;. Those constraints are not checked when binding ConstraintTypes
        /// since ConstraintTypes has not been set on I&lt;T&gt; at that point.
        /// </summary>
        private void CheckConstraintTypeConstraints(DiagnosticBag diagnostics)
        {
            var constraintTypes = this.ConstraintTypesNoUseSiteDiagnostics;

            // Witnesses are always generated from at least one constraint.
            // Thus, we should never get here and have zero constraints in play.
            Debug.Assert(0 < constraintTypes.Length);

            // Now, we behave just like a SourceTypeParameterSymbol.

            var corLibrary = this.ContainingAssembly.CorLibrary;
            var conversions = new TypeConversions(corLibrary);

            foreach (var constraintType in constraintTypes)
            {
                HashSet<DiagnosticInfo> useSiteDiagnostics = null;
                constraintType.AddUseSiteDiagnostics(ref useSiteDiagnostics);

                if (!diagnostics.Add(Location.None, useSiteDiagnostics))
                {
                    constraintType.CheckAllConstraints(conversions, Location.None, diagnostics);
                }
            }
        }

        private NamedTypeSymbol GetDefaultBaseType()
        {
            return this.ContainingAssembly.GetSpecialType(SpecialType.System_Object);
        }

        internal override void ForceComplete(SourceLocation locationOpt, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var incompletePart = _state.NextIncompletePart;
                switch (incompletePart)
                {
                    //@t-mawind We don't have any attributes on these, so don't
                    // try to complete them.

                    case CompletionPart.TypeParameterConstraints:
                        var constraintTypes = this.ConstraintTypesNoUseSiteDiagnostics;

                        // Nested type parameter references might not be valid in error scenarios.
                        //Debug.Assert(this.ContainingSymbol.IsContainingSymbolOfAllTypeParameters(this.ConstraintTypes));
                        //Debug.Assert(this.ContainingSymbol.IsContainingSymbolOfAllTypeParameters(ImmutableArray<TypeSymbol>.CreateFrom(this.Interfaces)));
                        Debug.Assert(this.ContainingSymbol.IsContainingSymbolOfAllTypeParameters(this.EffectiveBaseClassNoUseSiteDiagnostics));
                        Debug.Assert(this.ContainingSymbol.IsContainingSymbolOfAllTypeParameters(this.DeducedBaseTypeNoUseSiteDiagnostics));
                        break;

                    case CompletionPart.None:
                        return;

                    default:
                        // any other values are completion parts intended for other kinds of symbols
                        // @t-mawind Again, we don't have attributes, so note them complete.
                        _state.NotePartComplete(CompletionPart.All & ~CompletionPart.TypeParameterConstraints);
                        break;
                }

                _state.SpinWaitComplete(incompletePart, cancellationToken);
            }
        }

        // These are the main differences from SourceTypeParameterSymbolBase:

        /// <summary>
        /// Gets whether this parameter is constrained to be a value type.
        ///
        /// <para>
        /// Witnesses are always concept instances,
        /// which are encoded as value types: therefore this is set to true.
        /// </para>
        /// </summary>
        public override bool HasValueTypeConstraint => true;

        /// <summary>
        /// Injects synthesized attribute data for this implicit parameter.
        ///
        /// <para>
        /// This is overridden to return
        /// <see cref="AttributeDescription.ConceptWitnessAttribute"/> as an
        /// attribute.  Implicit parameters have no other attributes.
        /// </para>
        /// </summary>
        internal override void AddSynthesizedAttributes(ModuleCompilationState compilationState, ref ArrayBuilder<SynthesizedAttributeData> attributes)
        {
            base.AddSynthesizedAttributes(compilationState, ref attributes);

            CSharpCompilation compilation = this.DeclaringCompilation;
            AddSynthesizedAttribute(ref attributes, compilation.TrySynthesizeAttribute(WellKnownMember.System_Concepts_ConceptWitnessAttribute__ctor));
        }

        // As the name suggests, these are implicitly declared.
        // @t-mawind Is this correct?
        public override bool IsImplicitlyDeclared => true;
    }

    /// <summary>
    /// Type of synthesised concept-witness type parameters.
    /// </summary>
    internal sealed class SynthesizedWitnessParameterSymbol : SynthesizedWitnessParameterSymbolBase
    {
        private SourceNamedTypeSymbol _owner;

        /// <summary>
        /// Constructs a new SynthesizedWitnessParameterSymbol.
        /// </summary>
        /// <param name="name">
        /// The name of the type parameter.
        /// </param>
        /// <param name="clauseLocation">
        /// The location of the clause creating this witness.
        /// </param>
        /// <param name="ordinal">
        /// The ordinal of the type parameter.
        /// </param>
        /// <param name="owner">
        /// The symbol containing this type parameter.
        /// </param>
        internal SynthesizedWitnessParameterSymbol(string name, Location clauseLocation, int ordinal, SourceNamedTypeSymbol owner)
            : base(name, clauseLocation, ordinal)
        {
            _owner = owner;
        }

        public override TypeParameterKind TypeParameterKind => TypeParameterKind.Type;

        public override Symbol ContainingSymbol => _owner;

        public override bool HasConstructorConstraint
        {
            get
            {
                var constraints = this.GetDeclaredConstraints();
                return (constraints & TypeParameterConstraintKind.Constructor) != 0;
            }
        }

        public override bool HasReferenceTypeConstraint
        {
            get
            {
                var constraints = this.GetDeclaredConstraints();
                return (constraints & TypeParameterConstraintKind.ReferenceType) != 0;
            }
        }

        protected override ImmutableArray<TypeParameterSymbol> ContainerTypeParameters
        {
            get { return _owner.TypeParameters; }
        }

        protected override TypeParameterBounds ResolveBounds(ConsList<TypeParameterSymbol> inProgress, DiagnosticBag diagnostics)
        {
            var constraintTypes = _owner.GetTypeParameterConstraintTypes(this.Ordinal);
            return this.ResolveBounds(this.ContainingAssembly.CorLibrary, inProgress.Prepend(this), constraintTypes, false, this.DeclaringCompilation, diagnostics);
        }

        private TypeParameterConstraintKind GetDeclaredConstraints()
        {
            return _owner.GetTypeParameterConstraints(this.Ordinal);
        }
    }

    /// <summary>
    /// Type of synthesised concept-witness method type parameters.
    /// </summary>
    internal sealed class SynthesizedWitnessMethodParameterSymbol : SynthesizedWitnessParameterSymbolBase
    {
        /// <summary>
        /// The method symbol containing this type parameter.
        /// </summary>
        private readonly SourceMemberMethodSymbol _owner;

        /// <summary>
        /// Constructs a new SynthesizedWitnessMethodParameterSymbol.
        /// </summary>
        /// <param name="name">
        /// The name of the type parameter.
        /// </param>
        /// <param name="clauseLocation">
        /// The location of the clause creating this witness.
        /// </param>
        /// <param name="ordinal">
        /// The ordinal of the type parameter.
        /// </param>
        /// <param name="owner">
        /// The method symbol containing this type parameter.
        /// </param>
        public SynthesizedWitnessMethodParameterSymbol(string name, Location clauseLocation, int ordinal, SourceMemberMethodSymbol owner)
            : base(name, clauseLocation, ordinal)
        {
            _owner = owner;
        }

        public override TypeParameterKind TypeParameterKind => TypeParameterKind.Method;

        public override Symbol ContainingSymbol => _owner;

        public override bool HasConstructorConstraint
        {
            get
            {
                var constraints = this.GetDeclaredConstraints();
                return (constraints & TypeParameterConstraintKind.Constructor) != 0;
            }
        }

        public override bool HasReferenceTypeConstraint
        {
            get
            {
                var constraints = this.GetDeclaredConstraints();
                return (constraints & TypeParameterConstraintKind.ReferenceType) != 0;
            }
        }

        protected override ImmutableArray<TypeParameterSymbol> ContainerTypeParameters
        {
            get { return _owner.TypeParameters; }
        }

        protected override TypeParameterBounds ResolveBounds(ConsList<TypeParameterSymbol> inProgress, DiagnosticBag diagnostics)
        {
            var constraintTypes = _owner.GetTypeParameterConstraintTypes(this.Ordinal);
            return this.ResolveBounds(this.ContainingAssembly.CorLibrary, inProgress.Prepend(this), constraintTypes, false, this.DeclaringCompilation, diagnostics);
        }

        private TypeParameterConstraintKind GetDeclaredConstraints()
        {
            return _owner.GetTypeParameterConstraints(this.Ordinal);
        }
    }

    /// <summary>
    /// A witness type parameter for a method that either overrides a base
    /// type method or explicitly implements an interface method.
    /// </summary>
    /// <remarks>
    /// As to <see cref="SynthesizedWitnessMethodParameterSymbol"/> what
    /// <see cref="SourceOverridingMethodTypeParameterSymbol"/> is to 
    /// <see cref="SourceMemberContainerTypeSymbol"/>.
    /// </remarks>
    internal sealed class SynthesizedWitnessOverridingMethodTypeParameterSymbol : SynthesizedWitnessParameterSymbolBase
    {
        //@t-mawind mostly copied from SourceOverridingMethodTypeParameterSymbol.
        // This needs testing to make sure it's the right thing to do--
        // I'm sceptical.

        private readonly OverriddenMethodTypeParameterMapBase _map;

        /// <summary>
        /// Constructs a new SynthesizedWitnessMethodParameterSymbol.
        /// </summary>
        /// <param name="map">
        /// The overridden method map for the parent map.
        /// </param>
        /// <param name="name">
        /// The name of the type parameter.
        /// </param>
        /// <param name="clauseLocation">
        /// The location of the clause creating this witness.
        /// </param>
        /// <param name="ordinal">
        /// The ordinal of the type parameter.
        /// </param>
        public SynthesizedWitnessOverridingMethodTypeParameterSymbol(OverriddenMethodTypeParameterMapBase map, string name, Location clauseLocation, int ordinal)
            : base(name, clauseLocation, ordinal)
        {
            _map = map;
        }

        public SourceMemberMethodSymbol Owner => _map.OverridingMethod;

        public override TypeParameterKind TypeParameterKind => TypeParameterKind.Method;

        public override Symbol ContainingSymbol => this.Owner;

        public override bool HasConstructorConstraint
        {
            get
            {
                var typeParameter = this.OverriddenTypeParameter;
                return ((object)typeParameter != null) && typeParameter.HasConstructorConstraint;
            }
        }

        public override bool HasReferenceTypeConstraint
        {
            get
            {
                var typeParameter = this.OverriddenTypeParameter;
                return ((object)typeParameter != null) && typeParameter.HasReferenceTypeConstraint;
            }
        }

        protected override ImmutableArray<TypeParameterSymbol> ContainerTypeParameters
        {
            get { return this.Owner.TypeParameters; }
        }

        protected override TypeParameterBounds ResolveBounds(ConsList<TypeParameterSymbol> inProgress, DiagnosticBag diagnostics)
        {
            var typeParameter = this.OverriddenTypeParameter;
            if ((object)typeParameter == null)
            {
                return null;
            }

            var map = _map.TypeMap;
            Debug.Assert(map != null);

            var constraintTypes = map.SubstituteTypesWithoutModifiers(typeParameter.ConstraintTypesNoUseSiteDiagnostics);
            return this.ResolveBounds(this.ContainingAssembly.CorLibrary, inProgress.Prepend(this), constraintTypes, true, this.DeclaringCompilation, diagnostics);
        }

        /// <summary>
        /// The type parameter to use for determining constraints. If there is a base
        /// method that the owner method is overriding, the corresponding type
        /// parameter on that method is used. Otherwise, the result is null.
        /// </summary>
        private TypeParameterSymbol OverriddenTypeParameter
        {
            get
            {
                return _map.GetOverriddenTypeParameter(this.Ordinal);
            }
        }
    }
}