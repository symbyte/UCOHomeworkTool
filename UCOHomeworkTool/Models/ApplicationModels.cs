﻿using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Entity;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Web;

namespace UCOHomeworkTool.Models
{
    //static RNG members
    public static class Rand
    {
        private static readonly Random random = new Random();
        private static readonly object syncLock = new object();
        public static int RandomNumber(int min, int max)
        {
            return random.Next(min, max);
        }
    }
    public class Course
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public virtual List<Assignment> Templates { get; set; }
        public virtual List<Assignment> Assignments { get; set; }
        public virtual List<Student> Students { get; set; }
        public virtual Teacher Teacher { get; set; }
        public bool EnrollStudent(ApplicationDbContext context, Student student)
        {
            bool enrolled = true;
            var existingStudent = Students.Where(s => s.Id == student.Id).FirstOrDefault();
            if (existingStudent == null)
            {
                enrolled = false;
            }
            //add student to course
            if (!enrolled)
            {
                this.Students.Add(student);
                student.CoursesTaking.Add(this);
                context.SaveChanges();
            }
            return enrolled;
        }
        public Stream ExportGrades()
        {
            //create excel workbook object
            //filter assignment list
            using (var package = new ExcelPackage())
            {
                var distinctAssignmentNumbers = Assignments.Select(a => a.AssignmentNumber).Distinct().ToList().OrderBy(a => a);
                foreach (var assignmentNumber in distinctAssignmentNumbers)
                {
                    var currentAssignments = Assignments.Where(a => a.AssignmentNumber == assignmentNumber).OrderBy(a => a.Student.LastName).ToList();
                    //create new book for this set of assignments
                    var worksheet = package.Workbook.Worksheets.Add(string.Format("Assignment {0}", assignmentNumber));
                    int colIndex = 1;
                    int rowIndex = 1;
                    worksheet.Cells[rowIndex, colIndex++].Value = "Last Name";
                    worksheet.Cells[rowIndex, colIndex++].Value = "First Name";
                    worksheet.Cells[rowIndex, colIndex++].Value = "Student ID";
                    foreach (var prob in currentAssignments.First().Problems)
                    {
                        worksheet.Cells[rowIndex, colIndex++].Value = string.Format("Problem {0} Grade", prob.ProblemNumber);
                    }
                    worksheet.Cells[rowIndex, colIndex++].Value = "Assignment Grade";

                    //parse assignment for data to add to excel wookbook
                    foreach (var assignment in currentAssignments)
                    {
                        colIndex = 1;
                        worksheet.Cells[++rowIndex, colIndex++].Value = assignment.Student.LastName;
                        worksheet.Cells[rowIndex, colIndex++].Value = assignment.Student.FirstName;
                        worksheet.Cells[rowIndex, colIndex++].Value = assignment.Student.UserName;
                        foreach (var prob in assignment.Problems)
                        {
                            worksheet.Cells[rowIndex, colIndex++].Value = prob.GetGrade(); 
                        }
                        worksheet.Cells[rowIndex, colIndex++].Value = assignment.Grade;
                    }
                    worksheet.Cells.AutoFitColumns();
                }
                //convert to memory stream for easy return from controller as FileStreamResult
                MemoryStream ms = new MemoryStream(package.GetAsByteArray());
                return ms;
            }
        }
    }
    public class Assignment
    {
        public Assignment() { }
        public Assignment(Assignment toCopy)
        {
            AssignmentNumber = toCopy.AssignmentNumber;
            Course = toCopy.Course;
            Problems = new List<Problem>();
            foreach (var prob in toCopy.Problems)
            {
                var newProb = new Problem(prob);
                Problems.Add(newProb);
            }
        }
        public Assignment(Assignment toCopy, List<Problem> problemsToAssign)
        {
            AssignmentNumber = toCopy.AssignmentNumber;
            Course = toCopy.Course;
            Problems = new List<Problem>();
            int newProbNum = 1;
            foreach (var prob in problemsToAssign)
            {
                var newProb = new Problem(prob);
                newProb.ProblemNumber = newProbNum++;
                Problems.Add(newProb);
            }
        }

        public int Id { get; set; }
        public int AssignmentNumber { get; set; }
        public virtual List<Problem> Problems { get; set; }
        public virtual Course Course { get; set; }
        public virtual ApplicationUser Student { get; set; }
        public double Grade { get; set; }
        public void GradeAssignment()
        {
            double totalPoints = 20.0;
            double aggregateModifier = 0;
            var modifierSegment = 1.0 / Problems.Count;
            foreach (var prob in Problems)
            {
                if (prob.AllResponsesCorrect())
                {
                    switch (prob.TrysRemaining)
                    {
                        case 1:
                            aggregateModifier += modifierSegment * .7;
                            break;
                        case 0:
                            aggregateModifier += modifierSegment * .5;
                            break;
                        default:
                            aggregateModifier += modifierSegment;
                            break;
                    }

                }
            }
            this.Grade = Math.Round(totalPoints * aggregateModifier, 2, MidpointRounding.AwayFromZero);
        }
        public bool MakeAssignment(List<int> probids, ApplicationDbContext db)
        {
            //removing from the db all problems that have does not have its id in the probids list and is currently marked as assigned
            var currCourse = db.Courses.Find(this.Course.Id);
            var toRemove = db.Assignments.Where(a => a.AssignmentNumber == this.AssignmentNumber && this.Course.Id == a.Course.Id && a.Student != null).ToList();
            foreach (var assignment in toRemove)
            {
                var problemsToRemove = assignment.Problems.Except(assignment.Problems.Where(p => p.IsAssigned == true && probids.Contains(p.GeneratedFrom))).ToList();
                foreach (var prob in problemsToRemove)
                {
                    foreach (var given in prob.Givens.ToList())
                    {
                        db.Givens.Remove(given);
                    }
                    foreach (var resp in prob.Responses.ToList())
                    {
                        db.Responses.Remove(resp);
                    }
                    db.Problems.Remove(prob);
                    db.SaveChanges();
                }
                //if assignment is empty, delete it
                if (db.Assignments.Find(assignment.Id).Problems.Count == 0)
                {
                    db.Assignments.Remove(assignment);
                    db.SaveChanges();
                }
            }
            bool success = true;
            var probsToAssign = this.Problems.Where(prob => probids.Contains(prob.Id) && prob.IsAssigned == false).ToList();
            if (probsToAssign.Count != 0)
            {
                foreach (var student in Course.Students)
                {
                    var existingAssignment = Course.Assignments.Where(a => a.Student.Id == student.Id && a.AssignmentNumber == this.AssignmentNumber).FirstOrDefault();
                    if (existingAssignment == null)
                    {
                        var newAssignment = new Assignment(this, probsToAssign);
                        newAssignment.Student = student;
                        newAssignment.Course = currCourse;
                        currCourse.Assignments.Add(newAssignment);
                        db.SaveChanges();
                    }
                    else
                    {
                        foreach (var prob in probsToAssign)
                        {
                            prob.ProblemNumber = existingAssignment.Problems.Count + 1;
                            var newProb = new Problem(prob);
                            existingAssignment.Problems.Add(newProb);
                        }
                    }
                }

            }
            foreach (var prob in probsToAssign)
            {
                prob.IsAssigned = true;
            }
            var notAssigned = this.Problems.Except(probsToAssign).Except(this.Problems.Where(prob => probids.Contains(prob.Id))).ToList();
            foreach (var prob in notAssigned)
            {
                prob.IsAssigned = false;
            }
            return success;
        }
    }
    public class Problem
    {
        public Problem() { IsAssigned = false; }
        public Problem(Problem toCopy)
        {
            IsAssigned = true;
            Description = toCopy.Description;
            ProblemNumber = toCopy.ProblemNumber;
            Givens = new List<Given>();
            GeneratedFrom = toCopy.Id;
            TrysRemaining = 5;
            foreach (GivenTemplate given in toCopy.Givens)
            {
                var newGiven = new Given(given);
                newGiven.Problem = this;
                Givens.Add(newGiven);
            }
            Responses = new List<Response>();
            foreach (var resp in toCopy.Responses)
            {
                var newResp = new Response(resp);
                //setting expected to whatever value is stored in the template
                newResp.Expected = resp.Expected;
                //overriding the default only if an applicable equation exists to calculate the expected value
                newResp.calculation = resp.calculation;
                newResp.setExpected(this.Givens);
                //nullify the delegate pointer for the calculation to avoid storing an redundant pointer in the DB
                //this allows us to only keep a reference to the calculation in the template for the assignment
                newResp.calculation = null;
                newResp.Problem = this;
                Responses.Add(newResp);
            }
        }
        public double GetGrade()
        {
            if (AllResponsesCorrect())
            {
                switch (TrysRemaining)
                {
                    case 1:
                        return 0.5;
                    case 2:
                        return 0.7;
                    default:
                        return 1.0;
                }
            }
            return 0.0;
        }
        public bool AllResponsesCorrect()
        {
            bool allCorrect = true;
            foreach (var response in this.Responses)
            {
                switch (this.TrysRemaining)
                {
                    case 5:
                        allCorrect = false;
                        break;
                    case 4:
                        allCorrect = response.Expected.Equals(response.FirstAttempt);
                        break;
                    case 3:
                        allCorrect = response.Expected.Equals(response.SecondAttempt);
                        break;
                    case 2:
                        allCorrect = response.Expected.Equals(response.ThirdAttempt);
                        break;
                    case 1:
                        allCorrect = response.Expected.Equals(response.FourthAttempt);
                        break;
                    case 0:
                        allCorrect = response.Expected.Equals(response.FifthAttempt);
                        break;
                }
            }
            return allCorrect;
        }
        public int Id { get; set; }
        public int ProblemNumber { get; set; }
        public int GeneratedFrom { get; set; }
        public int TrysRemaining { get; set; }
        public string Description { get; set; }
        public virtual List<Given> Givens { get; set; }
        public virtual List<Response> Responses { get; set; }
        public Boolean IsAssigned { get; set; }
    }
    public class ProblemDiagram
    {
        public int Id { get; set; }
        public int ProblemId { get; set; }
        [NotMapped]
        public Image Diagram { get; set; }
        public byte[] ImageContent
        {
            get
            {
                MemoryStream ms = new MemoryStream();
                Diagram.Save(ms, Diagram.RawFormat);
                return ms.ToArray();
            }
            set
            {
                MemoryStream ms = new MemoryStream(value);
                Diagram = Image.FromStream(ms);
            }
        }
    }
    public class Given
    {
        public Given() { }
        public Given(GivenTemplate toCopy)
        {
            Label = toCopy.Label;
            Random rand = new Random();
            Value = Rand.RandomNumber(toCopy.minRange, toCopy.maxRange);
        }
        public int Id { get; set; }
        public string Label { get; set; }
        public int Value { get; set; }
        public virtual Problem Problem { get; set; }
    }
    public class GivenTemplate : Given
    {
        public int minRange { get; set; }
        public int maxRange { get; set; }
    }
    public class Response
    {
        public Response() { }
        public Response(Response toCopy)
        {
            Label = toCopy.Label;
        }
        public delegate void CalculateResponseDelegate(List<Given> givens, Response toCalculate);
        public byte[] delegatePointer { get; set; }
        [NotMapped]
        public CalculateResponseDelegate calculation
        {
            get
            {
                if (delegatePointer != null && delegatePointer.Count() > 0)
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    using (var stream = new MemoryStream(delegatePointer))
                    {
                        return formatter.Deserialize(stream) as CalculateResponseDelegate;
                    }
                }
                return null;
            }
            set
            {
                if (value == null)
                {
                    delegatePointer = null;
                }
                else
                {
                    BinaryFormatter formatter = new BinaryFormatter();
                    using (var stream = new MemoryStream())
                    {
                        formatter.Serialize(stream, value);
                        delegatePointer = stream.ToArray();
                    }
                }

            }
        }
        public void setExpected(List<Given> givens)
        {
            if (calculation != null)
                calculation(givens, this);
        }
        public int Id { get; set; }
        public string Label { get; set; }
        public double Expected { get; set; }
        public double FirstAttempt { get; set; }
        public double SecondAttempt { get; set; }
        public double ThirdAttempt { get; set; }
        public double FourthAttempt { get; set; }
        public double FifthAttempt { get; set; }

        public virtual Problem Problem { get; set; }
    }
}