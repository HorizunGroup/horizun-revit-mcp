;;; ---------------------------------------------------------------------------
;;; Horizun DWG extractor - runs inside accoreconsole, the headless AutoCAD that
;;; ships with the AutoCAD the user already has installed.
;;;
;;; WHY THIS EXISTS. Revit's imported geometry carries no string, no block name,
;;; no entity handle and no external reference: the bridge measured that and says
;;; so. Everything a conversion needs to be traceable - what a symbol IS, what a
;;; unit is CALLED, which drawing a line came from - lives in exactly those
;;; fields. This reads them from the file itself.
;;;
;;; IT NEVER WRITES. It opens nothing for output but its own report, issues no
;;; command that modifies the database, and the caller runs it against a COPY.
;;;
;;; THE OUTPUT IS LINE-ORIENTED AND TAB-SEPARATED, not JSON: AutoLISP has no
;;; escaping worth trusting for JSON, and a drawing's text is full of quotes and
;;; backslashes. Every field that could carry a tab or a newline is escaped here
;;; and unescaped on the other side.
;;;
;;;   H  <key> <value>                     header variable
;;;   L  <name> <color> <linetype> <flags> layer
;;;   K  <name> <flags> <xrefpath>         block table record (K for blocK)
;;;   Y  <name> <blockrecord>              layout
;;;   E  <owner> <handle> <type> <layer> <data...>   entity
;;;   A  <ownerhandle> <tag> <value>       attribute of the INSERT above it
;;;   an INSERT row ends: <reference name> <effective name> <how: repdata|reptag|definition|empty> <props>
;;;
;;; The walk is entnext over every block table record, which is the only walk
;;; that sees ALL of it: model space, every paper space, every block definition,
;;; and the attributes and vertices that hang off an entity as subentities.
;;; ---------------------------------------------------------------------------

(defun hz-esc (s / i c out)
  (if (null s)
    ""
    (progn
      (setq out "" i 1)
      (while (<= i (strlen s))
        (setq c (substr s i 1))
        (setq out
          (cond
            ((= c "\t") (strcat out "\\t"))
            ((= c "\n") (strcat out "\\n"))
            ((= c "\r") (strcat out "\\r"))
            ((= c "\\") (strcat out "\\\\"))
            (T (strcat out c))))
        (setq i (1+ i)))
      out)))

(defun hz-num (x)
  (if (numberp x) (rtos x 2 6) ""))

(defun hz-pt (p)
  (if p
    (strcat (hz-num (car p)) "\t" (hz-num (cadr p)) "\t"
            (hz-num (if (caddr p) (caddr p) 0.0)))
    "\t\t"))

(defun hz-dxf (code ed / v)
  (setq v (cdr (assoc code ed)))
  (if v v nil))

(defun hz-str (code ed / v)
  (setq v (cdr (assoc code ed)))
  (if v (hz-esc v) ""))

(defun hz-n (code ed / v)
  (setq v (cdr (assoc code ed)))
  (if v (hz-num v) ""))

;;; A light polyline's vertices, "x,y;x,y;...". Its own form for the same
;;; reason as hz-hatch: hz-entity has to stay short enough to be one script line.
(defun hz-lwpts (ed / pts)
  (setq pts "")
  (foreach item ed
    (if (= (car item) 10)
      (setq pts (strcat pts (if (= pts "") "" ";")
                        (hz-num (cadr item)) "," (hz-num (caddr item))))))
  pts)

;;; A HATCH's boundary travels as its own group codes, in order, from the first
;;; loop (92) to the seed points (98): decoding edge types in AutoLISP is where a
;;; reader goes wrong quietly, and the other side can be tested. Its own form,
;;; because a script feeds each form as ONE line and a longer hz-entity never
;;; completed (measured: 2,051 characters, no output, accoreconsole waiting).
(defun hz-hatch (ed / pts seen)
  (setq pts "" seen nil)
  (foreach item ed
    (if (= (car item) 92) (setq seen T))
    (if (= (car item) 98) (setq seen nil))
    (if (and seen (member (car item) '(92 93 72 73 10 11 40 42 50 51 97)))
      (setq pts (strcat pts (if (= pts "") "" ";") (itoa (car item)) ":"
                        (if (listp (cdr item))
                          (strcat (hz-num (cadr item)) "," (hz-num (caddr item)))
                          (hz-num (cdr item)))))))
  pts)

;;; One entity, with the fields that matter for its type. Anything not named
;;; here still gets a line: an unrecognised type is a FACT about the drawing,
;;; and a reader that silently drops what it does not model is how a conversion
;;; comes back 40% complete and says nothing.
;;; DYNAMIC BLOCKS. An instance of a dynamic block is an INSERT of an ANONYMOUS
;;; definition (*U11, *U25 ...) whose number AutoCAD reassigns on edit; the name a
;;; person gave the block lives elsewhere. Two independent places, without ActiveX
;;; (accoreconsole has none):
;;;   repdata  the INSERT's extension dictionary AcDbBlockRepresentation/AcDbRepData
;;;            points (340) at the dynamic definition's block record;
;;;   reptag   the anonymous definition's xdata AcDbBlockRepBTag carries (1005) the
;;;            handle of the definition it was generated from.
;;; The reference name (*U11) is still written in its own field: the two are
;;; different facts and neither replaces the other.
(defun hz-dsub (d key / m)
  (if d (progn (setq m (member (cons 3 key) (entget d))) (if m (cdr (assoc 360 m))))))

(defun hz-dyn-rep (ed / rd)
  (setq rd (hz-dsub (hz-dsub (cdr (assoc 360 ed)) "AcDbBlockRepresentation") "AcDbRepData"))
  (if rd (cdr (assoc 340 (entget rd)))))

(defun hz-dyn-tag (name / br rec xd h)
  (setq br (tblobjname "BLOCK" name))
  (if br
    (progn
      (setq rec (entget (cdr (assoc 330 (entget br))) '("AcDbBlockRepBTag")))
      (setq xd (cdr (assoc -3 rec)))
      (if xd (setq h (cdr (assoc 1005 (cdr (car xd))))))
      (if h (handent h)))))

;;; definition  the INSERT references a dynamic definition DIRECTLY (an instance in
;;;             its default state, or one RESETBLOCK put back there): its own name is
;;;             the effective name. MEASURED: without this, a reset instance lost its
;;;             effective name and with it its identity.
(defun hz-dyn-self (name / br)
  (setq br (tblobjname "BLOCK" name))
  (if br (hz-dsub (cdr (assoc 360 (entget (cdr (assoc 330 (entget br)))))) "ACAD_ENHANCEDBLOCK")))

(defun hz-dyn-name (ed / r tg)
  (setq r (hz-dyn-rep ed))
  (if r
    (list "repdata" (cdr (assoc 2 (entget r))))
    (progn
      (setq tg (hz-dyn-tag (cdr (assoc 2 ed))))
      (if tg
        (list "reptag" (cdr (assoc 2 (entget tg))))
        (if (hz-dyn-self (cdr (assoc 2 ed))) (list "definition" (cdr (assoc 2 ed))) nil)))))

;;; The three trailing INSERT fields, kept out of hz-entity so that form stays short
;;; enough for accoreconsole to read as one script line.
(defun hz-dyn-fields (ed / dyn)
  (setq dyn (hz-dyn-name ed))
  (strcat (if dyn (hz-esc (cadr dyn)) "") "\t" (if dyn (car dyn) "") "\t" (hz-dyn-props ed)))

;;; The values set ON THIS INSTANCE (visibility state, flip, angle, distance ...),
;;; as AutoCAD records them in the instance's ACAD_ENHANCEDBLOCKHISTORY: a name
;;; (300) followed by its value. GRIP* entries are grip positions, not properties.
;;; name=value pairs joined by ';'. A property never set on the instance keeps the
;;; definition's default and is simply absent here - absent is not "none".
(defun hz-dyn-props (ed / h out nm v)
  (setq h (hz-dsub (hz-dsub (hz-dsub (cdr (assoc 360 ed)) "AcDbBlockRepresentation") "AppDataCache")
                   "ACAD_ENHANCEDBLOCKHISTORY"))
  (setq out "" nm nil)
  (if h
    (foreach item (entget h)
      (cond
        ((= (car item) 300) (setq nm (cdr item)))
        ((and nm (member (car item) '(1 40 70 10 11)))
         (setq v (cond ((= (car item) 1) (cdr item))
                       ((listp (cdr item)) (strcat (hz-num (cadr item)) "," (hz-num (caddr item))))
                       (T (hz-num (cdr item)))))
         (if (not (wcmatch nm "GRIP*"))
           (setq out (strcat out (if (= out "") "" ";") (hz-esc nm) "=" (hz-esc v))))
         (setq nm nil)))))
  out)

;;; ATTDEF rows, kept out of hz-entity so that form stays under the one-line limit.
(defun hz-attdef (f base ed)
  (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                      (hz-n 40 ed) "\t" (hz-n 50 ed) "\t"
                      (hz-str 2 ed) "\t" (hz-str 1 ed)) f))

(defun hz-entity (f owner ed / typ h lay base)
  (setq typ (cdr (assoc 0 ed))
        h   (cdr (assoc 5 ed))
        lay (cdr (assoc 8 ed)))
  (setq base (strcat "E\t" owner "\t" (if h h "") "\t" typ "\t" (hz-esc (if lay lay "")) "\t"))
  (cond
    ((= typ "TEXT")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-n 40 ed) "\t" (hz-n 50 ed) "\t" (hz-str 1 ed)) f))
    ((= typ "MTEXT")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-n 40 ed) "\t" (hz-n 50 ed) "\t" (hz-str 1 ed)) f))
    ((= typ "ATTDEF") (hz-attdef f base ed))
    ((= typ "INSERT")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-n 50 ed) "\t"
                         (hz-n 41 ed) "\t" (hz-n 42 ed) "\t" (hz-n 43 ed) "\t"
                         (hz-str 2 ed) "\t" (hz-dyn-fields ed)) f))
    ((= typ "LINE")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-pt (cdr (assoc 11 ed)))) f))
    ((= typ "CIRCLE")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t" (hz-n 40 ed)) f))
    ((= typ "ARC")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t" (hz-n 40 ed) "\t"
                         (hz-n 50 ed) "\t" (hz-n 51 ed)) f))
    ((= typ "LWPOLYLINE")
     (write-line (strcat base (hz-n 90 ed) "\t" (hz-n 70 ed) "\t" (hz-lwpts ed)) f))
    ((= typ "LEADER")
     (write-line (strcat base (hz-lwpts ed)) f))
    ((= typ "HATCH")
     (write-line (strcat base (hz-str 2 ed) "\t" (hz-n 70 ed) "\t" (hz-n 91 ed) "\t" (hz-hatch ed)) f))
    ((= typ "VERTEX")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed)))) f))
    ((= typ "ATTRIB")
     (write-line (strcat "A\t" owner "\t" (hz-str 2 ed) "\t" (hz-str 1 ed)) f))
    (T (write-line base f))))

;;; THE SPACES ARE REACHED BY tblobjname AND NOT BY tblsearch.
;;;
;;; Measured, because it is the difference between reading a drawing and reading
;;; a drawing with its contents removed:
;;;
;;;   (tblsearch "BLOCK" "*Model_Space")  -> nil
;;;   (tblobjname "BLOCK" "*Model_Space") -> the record
;;;   (ssget "_X" (410 . "Model"))        -> 2097 entities in this file
;;;
;;; The first walk used tblsearch and (tblnext "BLOCK"), neither of which returns
;;; a space, so it reported 61,807 lines of block DEFINITIONS and not one of the
;;; placements that assemble the drawing. Every symbol looked like it was at the
;;; origin because the only coordinates being read were block-local ones.
(defun hz-block (f name / e ed lastins bt)
  (setq bt (tblobjname "BLOCK" name))
  (setq e (if bt (entnext bt) (cdr (assoc -2 (tblsearch "BLOCK" name)))))
  (setq lastins name)
  (while e
    (setq ed (entget e))
    (if (= (cdr (assoc 0 ed)) "INSERT")
      (setq lastins (cdr (assoc 5 ed))))
    (if (= (cdr (assoc 0 ed)) "ATTRIB")
      (hz-entity f lastins ed)
      (hz-entity f name ed))
    (setq e (entnext e))))

;;; A SPACE IS READ OFF THE ENTITY, NOT OFF THE WALK.
;;;
;;; Measured on a real permit set: (entnext) from the record of *Model_Space and
;;; from the record of *Paper_Space both run on through the drawing's whole
;;; entity list, so 2,515 model-space placements came back a second time labelled
;;; as paper, and the sheet's own entities came back labelled as model. Group 67
;;; is 1 for an entity in paper space, and group 410 names its layout; that is
;;; what each row now says. The reader keeps the first row of each handle.
(defun hz-space (f name / e ed lastins bt sp lay)
  (setq bt (tblobjname "BLOCK" name))
  (setq e (if bt (entnext bt) nil))
  (setq lastins name)
  (while e
    (setq ed (entget e))
    (setq lay (cdr (assoc 410 ed)))
    (setq sp (if (= (cdr (assoc 67 ed)) 1)
               (strcat "*Paper_Space|" (hz-esc (if lay lay "")))
               "*Model_Space"))
    (if (= (cdr (assoc 0 ed)) "INSERT")
      (setq lastins (cdr (assoc 5 ed))))
    (if (= (cdr (assoc 0 ed)) "ATTRIB")
      (hz-entity f lastins ed)
      (hz-entity f sp ed))
    (setq e (entnext e))))

(defun hz-dump (path / f blk lay lst)
  (setq f (open path "w"))
  (write-line (strcat "H\tdwg\t" (hz-esc (getvar "DWGNAME"))) f)
  (write-line (strcat "H\tinsunits\t" (itoa (getvar "INSUNITS"))) f)
  (write-line (strcat "H\tmeasurement\t" (itoa (getvar "MEASUREMENT"))) f)
  (write-line (strcat "H\tlunits\t" (itoa (getvar "LUNITS"))) f)
  (write-line (strcat "H\textmin\t" (hz-pt (getvar "EXTMIN"))) f)
  (write-line (strcat "H\textmax\t" (hz-pt (getvar "EXTMAX"))) f)

  (setq lay (tblnext "LAYER" T))
  (while lay
    (write-line (strcat "L\t" (hz-esc (cdr (assoc 2 lay))) "\t"
                        (itoa (cdr (assoc 62 lay))) "\t"
                        (hz-esc (cdr (assoc 6 lay))) "\t"
                        (itoa (cdr (assoc 70 lay)))) f)
    (setq lay (tblnext "LAYER")))

  (setq lst '())
  (setq blk (tblnext "BLOCK" T))
  (while blk
    (write-line (strcat "K\t" (hz-esc (cdr (assoc 2 blk))) "\t"
                        (itoa (cdr (assoc 70 blk))) "\t"
                        (hz-esc (cdr (assoc 1 blk)))) f)
    (setq lst (cons (cdr (assoc 2 blk)) lst))
    (setq blk (tblnext "BLOCK")))

  (foreach b lst (hz-block f b))

  ;; THE SPACES ARE NOT IN THE BLOCK TABLE WALK.
  ;;
  ;; (tblnext "BLOCK") never returns *Model_Space or *Paper_Space, so a walk built
  ;; only on it reads every block DEFINITION and none of the PLACEMENTS that
  ;; assemble the drawing. Measured on a real permit set: 61,807 lines of
  ;; definitions and not one entity of model space, which reads as a complete
  ;; extraction and is the drawing with its contents removed.
  ;;
  ;; tblsearch finds them by name even though tblnext skips them.
  (foreach sp (list "*Model_Space" "*Paper_Space" "*Paper_Space0" "*Paper_Space1"
                    "*Paper_Space2" "*Paper_Space3" "*Paper_Space4" "*Paper_Space5"
                    "*Paper_Space6" "*Paper_Space7" "*Paper_Space8" "*Paper_Space9")
    (if (tblobjname "BLOCK" sp)
      (progn (write-line (strcat "K\t" sp "\t0\t") f) (hz-space f sp))))

  (write-line "H\tdone\t1" f)
  (close f)
  (princ "\nHZ-DUMP-OK\n")
  (princ))
