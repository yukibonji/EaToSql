﻿module internal EaToSql.Reader

open EaToSql

[<Literal>]
let EaSampleFileName = "SampleModel_xmi2_1.xml"

/// The EA XMI types generated by the XmlProvider type provider
type EaUml = FSharp.Data.XmlProvider<EaSampleFileName, Global=true>

/// XML namespaces for readering EA XMI document elements
module NS =
    open System.Xml
    open System.Xml.Linq
    let Uml = XNamespace.Get("http://schema.omg.org/spec/UML/2.1")
    let Xmi = XNamespace.Get("http://schema.omg.org/spec/XMI/2.1")
    let EaUml = XNamespace.Get("http://www.sparxsystems.com/profiles/EAUML/1.0")
    let PrefixesAndUris = [ ("uml", Uml.NamespaceName); ("xmi", Xmi.NamespaceName); ("EAUML", EaUml.NamespaceName) ]
    let CreateManagerWithNamespaces (nt:XmlNameTable) =
        let nsMgr = XmlNamespaceManager(nt)
        PrefixesAndUris |> Seq.iter nsMgr.AddNamespace
        nsMgr

/// Extends the XContainer class with an xpath selection using our namespaces
type System.Xml.Linq.XContainer with
    member this.eaXPathSelect expression =
        let nav = System.Xml.XPath.Extensions.CreateNavigator this.Document
        let resolver = NS.CreateManagerWithNamespaces nav.NameTable
        System.Xml.XPath.Extensions.XPathSelectElements(this, expression, resolver)

/// Gets *all* packaged elements (even if they're in subfolders/subpackages)
let getAllPackagedElements (doc: EaUml.Xmi) =
    doc.XElement.eaXPathSelect "*//packagedElement"
    |> Seq.map (fun e -> EaUml.PackagedElement(e))

type OwnedOpNamedColumnsRefGetter = EaUml.Operation -> NamedColumnRefs
type PkgdElem = EaUml.PackagedElement
type AssociationPackagedElems = { Association:PkgdElem; Start: PkgdElem; End: PkgdElem }
type AssocationElementLocator = EaUml.Association -> AssociationPackagedElems

let createLinkAsocPkgElemGetterForDoc (doc: EaUml.Xmi) : AssocationElementLocator =
    let pkgElemsById = (getAllPackagedElements doc) |> Seq.map (fun pe -> (pe.Id, pe)) |> dict
    let get = getOrFail "failed to find element %s" pkgElemsById
    fun asoc -> { Association = (get asoc.Id); Start = (get asoc.Start); End = (get asoc.End) }

let createParamGetterForDoc (doc : EaUml.Xmi) : OwnedOpNamedColumnsRefGetter =
    let ownedOpsById =
        getAllPackagedElements doc
        |> Seq.collect (fun pe -> pe.OwnedOperations)
        |> Seq.map (fun op -> op.Id, op)
        |> dict
    fun (op: EaUml.Operation) ->
        let ownedOp = getOrFail "failed to find ownedOperation/columns for table operation %s" ownedOpsById op.Idref
        { NamedColumnRefs.Name = Named(ownedOp.Name)
          Columns = ownedOp.OwnedParameters
                    |> Seq.map (fun oop -> oop.Name)
                    |> Seq.cast<ColumnRef>
                    |> Seq.toList }

let attrToColumnDef (a: EaUml.Attribute) : ColumnDef =
    let isAutoNum =
        let pt = (a.Tags |> Array.tryFind (fun t -> t.Name = "property"))
        if pt.IsSome && pt.Value.Value.IsSome then pt.Value.Value.Value.Contains("AutoNum=1")
        else false

    let createDataType = DataType.Create (a.Properties.Type.Value, isAutoNum,
                                         length = a.Properties.Length,
                                         decimalScale = a.Coords.Scale, decimalPrec = a.Properties.Precision)

    { ColumnDef.Name = a.Name
      Nullable = (a.Properties.Duplicates = (Some false))
      DataType = createDataType }

/// Extracts table operations that have a given sterotype
let tableOpsForStereotype stereotype projection (e: EaUml.Element) =
    match e.Operations with
    | None -> failwithf "expected element %A to have operations (at least a PK!)" e.Name
    | Some o ->
        o.Operations
        |> Seq.filter (fun o -> o.Stereotype.Stereotype = stereotype)
        |> Seq.map projection

let pkForElement (oopGetter: OwnedOpNamedColumnsRefGetter) (elem: EaUml.Element) : PrimaryKey =
    tableOpsForStereotype "PK" (fun op -> oopGetter op) elem |> Seq.exactlyOne

let indexesForElement (oopGetter: OwnedOpNamedColumnsRefGetter) (elem: EaUml.Element) : Index list =
    tableOpsForStereotype "index" oopGetter elem |> Seq.toList

let uniquesForElement (oopGetter: OwnedOpNamedColumnsRefGetter) (elem: EaUml.Element) : Unique list =
    tableOpsForStereotype "unique" oopGetter elem |> Seq.toList

/// Turns a table association into a Relationship
let asocToRelationship  (oopGetter: OwnedOpNamedColumnsRefGetter)
                        (asocLocator: AssocationElementLocator)
                        (table: EaUml.Element)
                        (asoc: EaUml.Association) : Relationship =

    let asocPkgElems = asocLocator asoc
    let fkName = asocPkgElems.Association.OwnedEnd.Value.Name
    let sourceOperation = table.Operations.Value.Operations |> Array.filter (fun op -> op.Name = fkName) |> Seq.exactlyOne

    let memberEndDst = asocPkgElems.Association.MemberEnds |> Seq.find (fun me -> me.Idref.StartsWith("EAID_dst"))
    let targetOwnedAttr = asocPkgElems.Start.OwnedAttributes |> Seq.find (fun oa -> oa.Id = memberEndDst.Idref)
    let targetName = targetOwnedAttr.Name
    let targetOwnedOp = asocPkgElems.End.OwnedOperations |> Seq.find (fun oop -> oop.Name = targetName)

    { Relationship.Name = Named(fkName)
      SourceCols = (oopGetter sourceOperation).Columns
      Target = { TableName = asocPkgElems.End.Name
                 Columns = (targetOwnedOp.OwnedParameters |> Seq.map (fun op -> op.Name) |> Seq.toList) }
    }

let classElementToTable linkAsocGetter oopGetter (e: EaUml.Element) : Table =
    { Table.Name = e.Name.Value
      Columns = (e.Attributes.Value.Attributes |> Array.map attrToColumnDef |> Seq.toList)
      PrimaryKey = pkForElement oopGetter e
      Indexes = (indexesForElement oopGetter e) |> Seq.toList
      Uniques = (uniquesForElement oopGetter e) |> Seq.toList
      Relationships = if e.Links.IsNone then []
                      else e.Links.Value.Associations
                            |> Seq.filter (fun a -> a.Start = e.Idref.Value)
                            |> Seq.map (asocToRelationship oopGetter linkAsocGetter e)
                            |> Seq.toList }

let getTablesForDoc (doc: EaUml.Xmi) : Table seq =
    let oopLocator = createParamGetterForDoc doc
    let asocLocator = createLinkAsocPkgElemGetterForDoc doc
    doc.Extension.Elements
    |> Seq.filter (fun e -> e.Type = Some "uml:Class")
    |> Seq.map (classElementToTable asocLocator oopLocator)

let getDocForXmi (reader: System.IO.TextReader) = EaUml.Load(reader)

