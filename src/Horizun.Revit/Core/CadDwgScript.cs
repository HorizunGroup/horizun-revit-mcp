// -----------------------------------------------------------------------------
// Horizun Revit MCP - GENERATED FILE. Do not edit by hand.
//
// Generated from src/Horizun.Revit/Core/hz_dwg_extract.lsp by
// benchmarks/dwg-bim-2026-09-15/evidence/embed_lisp.py, which strips the
// comments and collapses each top-level form onto one line.
//
// WHY THE FORMS ARE SEPARATE AND WHY THEY ARE ONE LINE EACH. AutoCAD 2025 ships
// with SECURELOAD on, so (load "...") from a path the user has not trusted is
// refused - "File load canceled" - and adding a trusted path would be this
// bridge changing the user's AutoCAD configuration in order to read a file.
// LISP typed into a script is not subject to that, and a script is read a line
// at a time, so each top-level form has to fit on one.
//
// CadDwgScriptTests regenerates this from the .lsp and fails if the two differ.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    /// <summary>The extractor, as accoreconsole has to be fed it.</summary>
    public static class CadDwgScript
    {
        /// <summary>One top-level AutoLISP form per entry, comments stripped.</summary>
        public static readonly string[] Forms =
        {
            @"(defun hz-esc (s / i c out) (if (null s) """" (progn (setq out """" i 1) (while (<= i (strlen s)) (setq c (substr s i 1)) (setq out (cond ((= c ""\t"") (strcat out ""\\t"")) ((= c ""\n"") (strcat out ""\\n"")) ((= c ""\r"") (strcat out ""\\r"")) ((= c ""\\"") (strcat out ""\\\\"")) (T (strcat out c)))) (setq i (1+ i))) out)))",
            @"(defun hz-num (x) (if (numberp x) (rtos x 2 6) """"))",
            @"(defun hz-pt (p) (if p (strcat (hz-num (car p)) ""\t"" (hz-num (cadr p)) ""\t"" (hz-num (if (caddr p) (caddr p) 0.0))) ""\t\t""))",
            @"(defun hz-dxf (code ed / v) (setq v (cdr (assoc code ed))) (if v v nil))",
            @"(defun hz-str (code ed / v) (setq v (cdr (assoc code ed))) (if v (hz-esc v) """"))",
            @"(defun hz-n (code ed / v) (setq v (cdr (assoc code ed))) (if v (hz-num v) """"))",
            @"(defun hz-lwpts (ed / pts) (setq pts """") (foreach item ed (if (= (car item) 10) (setq pts (strcat pts (if (= pts """") """" "";"") (hz-num (cadr item)) "","" (hz-num (caddr item)))))) pts)",
            @"(defun hz-hatch (ed / pts seen) (setq pts """" seen nil) (foreach item ed (if (= (car item) 92) (setq seen T)) (if (= (car item) 98) (setq seen nil)) (if (and seen (member (car item) '(92 93 72 73 10 11 40 42 50 51 97))) (setq pts (strcat pts (if (= pts """") """" "";"") (itoa (car item)) "":"" (if (listp (cdr item)) (strcat (hz-num (cadr item)) "","" (hz-num (caddr item))) (hz-num (cdr item))))))) pts)",
            @"(defun hz-entity (f owner ed / typ h lay base) (setq typ (cdr (assoc 0 ed)) h (cdr (assoc 5 ed)) lay (cdr (assoc 8 ed))) (setq base (strcat ""E\t"" owner ""\t"" (if h h """") ""\t"" typ ""\t"" (hz-esc (if lay lay """")) ""\t"")) (cond ((= typ ""TEXT"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 40 ed) ""\t"" (hz-n 50 ed) ""\t"" (hz-str 1 ed)) f)) ((= typ ""MTEXT"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 40 ed) ""\t"" (hz-n 50 ed) ""\t"" (hz-str 1 ed)) f)) ((= typ ""ATTDEF"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 40 ed) ""\t"" (hz-n 50 ed) ""\t"" (hz-str 2 ed) ""\t"" (hz-str 1 ed)) f)) ((= typ ""INSERT"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 50 ed) ""\t"" (hz-n 41 ed) ""\t"" (hz-n 42 ed) ""\t"" (hz-n 43 ed) ""\t"" (hz-str 2 ed)) f)) ((= typ ""LINE"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-pt (cdr (assoc 11 ed)))) f)) ((= typ ""CIRCLE"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 40 ed)) f)) ((= typ ""ARC"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed))) ""\t"" (hz-n 40 ed) ""\t"" (hz-n 50 ed) ""\t"" (hz-n 51 ed)) f)) ((= typ ""LWPOLYLINE"") (write-line (strcat base (hz-n 90 ed) ""\t"" (hz-n 70 ed) ""\t"" (hz-lwpts ed)) f)) ((= typ ""HATCH"") (write-line (strcat base (hz-str 2 ed) ""\t"" (hz-n 70 ed) ""\t"" (hz-n 91 ed) ""\t"" (hz-hatch ed)) f)) ((= typ ""VERTEX"") (write-line (strcat base (hz-pt (cdr (assoc 10 ed)))) f)) ((= typ ""ATTRIB"") (write-line (strcat ""A\t"" owner ""\t"" (hz-str 2 ed) ""\t"" (hz-str 1 ed)) f)) (T (write-line base f))))",
            @"(defun hz-block (f name / e ed lastins bt) (setq bt (tblobjname ""BLOCK"" name)) (setq e (if bt (entnext bt) (cdr (assoc -2 (tblsearch ""BLOCK"" name))))) (setq lastins name) (while e (setq ed (entget e)) (if (= (cdr (assoc 0 ed)) ""INSERT"") (setq lastins (cdr (assoc 5 ed)))) (if (= (cdr (assoc 0 ed)) ""ATTRIB"") (hz-entity f lastins ed) (hz-entity f name ed)) (setq e (entnext e))))",
            @"(defun hz-space (f name / e ed lastins bt sp lay) (setq bt (tblobjname ""BLOCK"" name)) (setq e (if bt (entnext bt) nil)) (setq lastins name) (while e (setq ed (entget e)) (setq lay (cdr (assoc 410 ed))) (setq sp (if (= (cdr (assoc 67 ed)) 1) (strcat ""*Paper_Space|"" (hz-esc (if lay lay """"))) ""*Model_Space"")) (if (= (cdr (assoc 0 ed)) ""INSERT"") (setq lastins (cdr (assoc 5 ed)))) (if (= (cdr (assoc 0 ed)) ""ATTRIB"") (hz-entity f lastins ed) (hz-entity f sp ed)) (setq e (entnext e))))",
            @"(defun hz-dump (path / f blk lay lst) (setq f (open path ""w"")) (write-line (strcat ""H\tdwg\t"" (hz-esc (getvar ""DWGNAME""))) f) (write-line (strcat ""H\tinsunits\t"" (itoa (getvar ""INSUNITS""))) f) (write-line (strcat ""H\tmeasurement\t"" (itoa (getvar ""MEASUREMENT""))) f) (write-line (strcat ""H\tlunits\t"" (itoa (getvar ""LUNITS""))) f) (write-line (strcat ""H\textmin\t"" (hz-pt (getvar ""EXTMIN""))) f) (write-line (strcat ""H\textmax\t"" (hz-pt (getvar ""EXTMAX""))) f) (setq lay (tblnext ""LAYER"" T)) (while lay (write-line (strcat ""L\t"" (hz-esc (cdr (assoc 2 lay))) ""\t"" (itoa (cdr (assoc 62 lay))) ""\t"" (hz-esc (cdr (assoc 6 lay))) ""\t"" (itoa (cdr (assoc 70 lay)))) f) (setq lay (tblnext ""LAYER""))) (setq lst '()) (setq blk (tblnext ""BLOCK"" T)) (while blk (write-line (strcat ""K\t"" (hz-esc (cdr (assoc 2 blk))) ""\t"" (itoa (cdr (assoc 70 blk))) ""\t"" (hz-esc (cdr (assoc 1 blk)))) f) (setq lst (cons (cdr (assoc 2 blk)) lst)) (setq blk (tblnext ""BLOCK""))) (foreach b lst (hz-block f b)) (foreach sp (list ""*Model_Space"" ""*Paper_Space"" ""*Paper_Space0"" ""*Paper_Space1"" ""*Paper_Space2"" ""*Paper_Space3"" ""*Paper_Space4"" ""*Paper_Space5"" ""*Paper_Space6"" ""*Paper_Space7"" ""*Paper_Space8"" ""*Paper_Space9"") (if (tblobjname ""BLOCK"" sp) (progn (write-line (strcat ""K\t"" sp ""\t0\t"") f) (hz-space f sp)))) (write-line ""H\tdone\t1"" f) (close f) (princ ""\nHZ-DUMP-OK\n"") (princ))",
        };
    }
}
