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

;;; One entity, with the fields that matter for its type. Anything not named
;;; here still gets a line: an unrecognised type is a FACT about the drawing,
;;; and a reader that silently drops what it does not model is how a conversion
;;; comes back 40% complete and says nothing.
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
    ((= typ "ATTDEF")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-n 40 ed) "\t" (hz-n 50 ed) "\t"
                         (hz-str 2 ed) "\t" (hz-str 1 ed)) f))
    ((= typ "INSERT")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-n 50 ed) "\t"
                         (hz-n 41 ed) "\t" (hz-n 42 ed) "\t" (hz-n 43 ed) "\t"
                         (hz-str 2 ed)) f))
    ((= typ "LINE")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t"
                         (hz-pt (cdr (assoc 11 ed)))) f))
    ((= typ "CIRCLE")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t" (hz-n 40 ed)) f))
    ((= typ "ARC")
     (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) "\t" (hz-n 40 ed) "\t"
                         (hz-n 50 ed) "\t" (hz-n 51 ed)) f))
    ((= typ "LWPOLYLINE")
     (progn
       (setq pts "" )
       (foreach item ed
         (if (= (car item) 10)
           (setq pts (strcat pts (if (= pts "") "" ";")
                             (hz-num (cadr item)) "," (hz-num (caddr item))))))
       (write-line (strcat base (hz-n 90 ed) "\t" (hz-n 70 ed) "\t" pts) f)))
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
      (progn (write-line (strcat "K\t" sp "\t0\t") f) (hz-block f sp))))

  (write-line "H\tdone\t1" f)
  (close f)
  (princ "\nHZ-DUMP-OK\n")
  (princ))
