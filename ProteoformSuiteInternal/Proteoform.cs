﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ProteoformSuiteInternal
{
    public class Proteoform
    {
        public string accession { get; set; }
        public double modified_mass { get; set; }
        public int lysine_count { get; set; } = -1;
        public bool is_target { get; set; } = true;
        public bool is_decoy { get; set; } = false;
        public List<ProteoformRelation> relationships { get; set; } = new List<ProteoformRelation>();
        public ProteoformFamily family { get; set; }

        public Proteoform(string accession, double modified_mass, int lysine_count, bool is_target)
        {
            this.accession = accession;
            this.modified_mass = modified_mass;
            this.lysine_count = lysine_count;
            if (!is_target)
            {
                this.is_target = false;
                this.is_decoy = true;
            }
        }       public Proteoform(string accession)
        {
            this.accession = accession;
        }

        public List<Proteoform> get_connected_proteoforms()
        {
            return relationships.Where(r => r.peak.peak_accepted).SelectMany(r => r.connected_proteoforms).ToList();
        }
    }

    //Note ExperimentalProteoform is a bit of a misnomer. These are not experimental observations, but rather aggregated experimental
    //observations. Each NeuCodePair is an ExperimentalProteoform, but this class is used after accounting for missed lysines and monoisotopics.
    //However, I think this makes the programming a bit cleaner, since "Experimental-Theoretical" pairs should naturally be between 
    //"ExperimentalProteoform" and "TheoreticalProteoform" objects
    public class ExperimentalProteoform : Proteoform
    {
        private Component root;
        public List<Component> aggregated_components { get; set; } = new List<Component>();
        public List<Component> lt_quant_components { get; set; } = new List<Component>();
        public List<Component> hv_quant_components { get; set; } = new List<Component>();
        public bool accepted { get; set; } = true;
        public double agg_mass { get; set; } = 0;
        public double agg_intensity { get; set; } = 0;
        public double agg_rt { get; set; } = 0;
        public int observation_count
        {
            get { return aggregated_components.Count; }
        }
        public bool mass_shifted { get; set; } = false; //make sure in ET if shifting multiple peaks, not shifting same E > once. 

        public ExperimentalProteoform(string accession, Component root, List<Component> candidate_observations, List<Component> quantitative_observations, bool is_target) : base(accession)
        {
            this.root = root;
            this.aggregated_components.AddRange(candidate_observations.Where(p => this.includes(p, this.root)));
            this.calculate_properties();
            if(quantitative_observations.Count > 0)
            {
                this.lt_quant_components.AddRange(quantitative_observations.Where(r => this.includes(r, this, true)));
                if (Lollipop.neucode_labeled)
                    this.hv_quant_components.AddRange(quantitative_observations.Where(r => this.includes(r, this, false)));
            }
        }

        public ExperimentalProteoform(string accession, double modified_mass, int lysine_count, bool is_target) : base(accession)
        {
            this.aggregated_components = new List<Component>() { root };
            this.accession = accession;
            this.modified_mass = modified_mass;
            this.lysine_count = lysine_count;
            if (!is_target)
            {
                this.is_target = false;
                this.is_decoy = true;
            }
        }

        //for Tests
        public ExperimentalProteoform(string accession) : base(accession)
        {
            this.aggregated_components = new List<Component>() { root };
            this.accession = accession;
        }

        private void calculate_properties()
        {
            //if not neucode labeled, the intensity sum of overlapping charge states was calculated with all charge states.
            if (Lollipop.neucode_labeled)
            {
                this.agg_intensity = aggregated_components.Select(p => p.intensity_sum_olcs).Sum();
                this.agg_mass = aggregated_components.Select(p => (p.corrected_mass - Math.Round(p.corrected_mass - this.root.corrected_mass, 0) * Lollipop.MONOISOTOPIC_UNIT_MASS) * p.intensity_sum_olcs / this.agg_intensity).Sum(); //remove the monoisotopic errors before aggregating masses
                this.agg_rt = aggregated_components.Select(p => p.rt_apex * p.intensity_sum_olcs / this.agg_intensity).Sum();
            }
            else
            {
                this.agg_intensity = aggregated_components.Select(p => p.intensity_sum).Sum();
                this.agg_mass = aggregated_components.Select(p => (p.corrected_mass - Math.Round(p.corrected_mass - this.root.corrected_mass, 0) * Lollipop.MONOISOTOPIC_UNIT_MASS) * p.intensity_sum / this.agg_intensity).Sum(); //remove the monoisotopic errors before aggregating masses
                this.agg_rt = aggregated_components.Select(p => p.rt_apex * p.intensity_sum / this.agg_intensity).Sum();

            }
            if (root is NeuCodePair) this.lysine_count = ((NeuCodePair)this.root).lysine_count;
            this.modified_mass = this.agg_mass;
            this.accepted = this.aggregated_components.Count() >= Lollipop.min_agg_count;
        }

        //This aggregates based on lysine count, mass, and retention time all at the same time. Note that in the past we aggregated based on 
        //lysine count first, and then aggregated based on mass and retention time afterwards, which may give a slightly different root for the 
        //experimental proteoform because the precursor aggregation may shuffle the intensity order slightly. We haven't observed any negative
        //impact of this difference as of 160812. -AC
        public bool includes(Component candidate, Component root)
        {
            bool does_include = tolerable_rt(candidate, root.rt_apex) && tolerable_mass(candidate, root.corrected_mass);
            if (candidate is NeuCodePair) does_include = does_include && tolerable_lysCt((NeuCodePair)candidate, ((NeuCodePair)root).lysine_count);
            return does_include;
        }

        public bool includes(Component candidate, ExperimentalProteoform root, bool light)
        {
            double corrected_mass = root.agg_mass;
            if (!light)       
                corrected_mass = corrected_mass + root.lysine_count * Lollipop.NEUCODE_LYSINE_MASS_SHIFT;
            
            bool does_include = tolerable_rt(candidate, root.agg_rt) && tolerable_mass(candidate, corrected_mass);
            if (candidate is NeuCodePair) does_include = does_include && tolerable_lysCt((NeuCodePair)candidate, root.lysine_count);
            return does_include;
        }

        private bool tolerable_rt(Component candidate, double rt_apex)
        {
            return candidate.rt_apex >= rt_apex - Convert.ToDouble(Lollipop.retention_time_tolerance) &&
                candidate.rt_apex <= rt_apex + Convert.ToDouble(Lollipop.retention_time_tolerance);
        }

        private bool tolerable_lysCt(NeuCodePair candidate, int lysine_count)
        {
            int max_missed_lysines = Convert.ToInt32(Lollipop.missed_lysines);
            List<int> acceptable_lysineCts = Enumerable.Range(lysine_count - max_missed_lysines, max_missed_lysines * 2 + 1).ToList();
            return acceptable_lysineCts.Contains(candidate.lysine_count);
        }

        private bool tolerable_mass(Component candidate, double corrected_mass)
        {
            int max_missed_monoisotopics = Convert.ToInt32(Lollipop.missed_monos);
            List<int> missed_monoisotopics_range = Enumerable.Range(-max_missed_monoisotopics, max_missed_monoisotopics * 2 + 1).ToList();
            foreach (int missed_mono_count in missed_monoisotopics_range)
            {
                double shift = missed_mono_count * Lollipop.MONOISOTOPIC_UNIT_MASS;
                double shifted_mass = corrected_mass + shift;
                double mass_tolerance = shifted_mass / 1000000 * Convert.ToInt32(Lollipop.mass_tolerance);
                double low = shifted_mass - mass_tolerance;
                double high = shifted_mass + mass_tolerance;
                bool tolerable_mass = candidate.corrected_mass >= low && candidate.corrected_mass <= high;
                if (tolerable_mass) return true; //Return a true result immediately; acts as an OR between these conditions
            }
            return false;
        }

        public void shift_masses(int shift)
        {
            if (Lollipop.neucode_labeled)
            {
                foreach (Component c in this.aggregated_components)
                {
                    ((NeuCodePair)c).manual_mass_shift += shift * Lollipop.MONOISOTOPIC_UNIT_MASS;
                    ((NeuCodePair)c).neuCodeLight.manual_mass_shift += shift * Lollipop.MONOISOTOPIC_UNIT_MASS;
                    ((NeuCodePair)c).neuCodeHeavy.manual_mass_shift += shift * Lollipop.MONOISOTOPIC_UNIT_MASS;
                }
            }
            else //unlabeled
            {
                foreach (Component c in this.aggregated_components)
                {
                    c.manual_mass_shift += shift * Lollipop.MONOISOTOPIC_UNIT_MASS;
                }
            }
            this.mass_shifted = true; //if shifting multiple peaks @ once, won't shift same E more than once if it's in multiple peaks.
        }

        //Quantitative Class and Methods
        public class qVals
        {
            public double ratio { get; set; }
            public double intensity { get; set; }
            public double fraction { get; set; }
            public List<Component> light { get; set; }
            public List<Component> heavy { get; set; }
        }

        public weightedRatioIntensityVariance weightedRatioAndWeightedVariance(List<InputFile> inputFileList) //the inputFileList is a list of "quantitative" input files
        {
            List<qVals> quantitativeValues = new List<qVals>();
            weightedRatioIntensityVariance wRIV = new weightedRatioIntensityVariance();

            double squaredVariance = 0;

            inputFileList.ForEach(inFile =>
            {
                qVals q = new qVals();
                q.light = (from s in lt_quant_components where s.input_file == inFile select s).ToList();
                q.heavy = (from s in hv_quant_components where s.input_file == inFile select s).ToList();
                double numerator = (from s in lt_quant_components where s.input_file == inFile select s.intensity_sum).Sum();
                double denominator = (from s in hv_quant_components where s.input_file == inFile select s.intensity_sum).Sum();
                if (numerator == 0)
                    numerator = numerator + 1000;//adding 1000 to deal with missing values
                if (denominator == 0)
                    denominator = denominator + 1000;//adding 1000 to deal with missing values
                q.ratio = Math.Log(numerator / denominator, 2);
                q.intensity = numerator + denominator;

                if ((q.light.Count() + q.heavy.Count()) > 0)
                    quantitativeValues.Add(q);
            });

            wRIV.intensity = quantitativeValues.Sum(s => s.intensity);

            if (wRIV.intensity > 0)
            {
                quantitativeValues.ForEach(q => {
                    wRIV.ratio = wRIV.ratio + q.ratio * q.intensity / wRIV.intensity;
                    q.fraction = (double)q.intensity / wRIV.intensity;
                });
                quantitativeValues.ForEach(q => {
                    squaredVariance = squaredVariance + q.fraction * Math.Pow((q.ratio - wRIV.ratio), 2);
                });
                wRIV.pValue = pValueFromPermutation(quantitativeValues, wRIV.intensity, wRIV.ratio);
            }

            if (squaredVariance > 0)
                wRIV.variance = Math.Pow(squaredVariance, 0.5);

            return wRIV;
        }

        private double pValueFromPermutation(List<qVals> quantitativeValues, double totalIntensity, double realRatio)
        {
            double pValue = 0;
            int maxPermutations = 1000;
            ConcurrentBag<double> permutedRatios = new ConcurrentBag<double>();

            Parallel.For(0, maxPermutations, i =>
            {
                double someRatio = 0;
                quantitativeValues.ForEach(q =>
                {
                    IList<Component> combined = new List<Component>();
                    combined = q.light.Concat(q.heavy).ToList();
                    combined.Shuffle();
                    double numerator = (from s in combined.Take(q.light.Count()) select s.intensity_sum).Sum();
                    double denominator = (from s in combined.Skip(q.light.Count()).Take(q.heavy.Count()) select s.intensity_sum).Sum();

                    if (numerator == 0) numerator = numerator + 1000; //adding 1000 to deal with missing values
                    if (denominator == 0) denominator = denominator + 1000; //adding 1000 to deal with missing values
                    someRatio = someRatio + Math.Log(numerator / denominator, 2) * q.intensity / totalIntensity;
                });
                permutedRatios.Add(someRatio);
            });

            if (realRatio > 0)
                pValue = (double)permutedRatios.Count(x => x > realRatio) / permutedRatios.Count();
            else
                pValue = (double)permutedRatios.Count(x => x < realRatio) / permutedRatios.Count();
            return pValue;
        }
    }

    public class TheoreticalProteoform : Proteoform
    {
        public string name { get; set; }
        public string description { get; set; }
        public string fragment { get; set; }
        public int begin { get; set; }
        public int end { get; set; }
        public double unmodified_mass { get; set; }
        private string sequence { get; set; }
        public List<GoTerm> goTerms { get; set; } = null;
        public PtmSet ptm_set { get; set; } = new PtmSet(new List<Ptm>());
        public List<Ptm> ptm_list { get { return ptm_set.ptm_combination.ToList(); } }
        public double ptm_mass { get { return ptm_set.mass; } }
        public string ptm_descriptions
        {
            get { return ptm_list_string(); }
        }
        public List<Psm> psm_list { get; set; } = new List<Psm>();
        private int _psm_count_BU;
        private int _psm_count_TD;
        public int psm_count_BU { set { _psm_count_BU = value; } get { if (!Lollipop.opened_results_originally) return psm_list.Where(p => p.psm_type == PsmType.BottomUp).ToList().Count; else return _psm_count_BU; } } 
        public int psm_count_TD { set { _psm_count_TD = value; } get { if (!Lollipop.opened_results_originally) return psm_list.Where(p => p.psm_type == PsmType.TopDown).ToList().Count; else return _psm_count_TD; } } 
        public string of_interest { get; set; } = "";

        public TheoreticalProteoform(string accession, string description, string name, string fragment, int begin, int end, double unmodified_mass, int lysine_count, List<GoTerm> goTerms, PtmSet ptm_set, double modified_mass, bool is_target) : 
            base(accession, modified_mass, lysine_count, is_target)
        {
            this.accession = accession;
            this.description = description;
            this.name = name;
            this.fragment = fragment;
            this.begin = begin;
            this.end = end;
            this.ptm_set = ptm_set;
            this.unmodified_mass = unmodified_mass;
        }

        //for Tests
        public TheoreticalProteoform(string accession): base(accession)
        {
            this.accession = accession;
        }
        //for Tests
        public TheoreticalProteoform(string accession, double modified_mass, int lysine_count, bool is_target) : base (accession,  modified_mass,  lysine_count,  is_target)
        {
            this.accession = accession;
            this.modified_mass = modified_mass;
            this.lysine_count = lysine_count;
            if (!is_target)
            {
                this.is_target = false;
                this.is_decoy = true;
            }
        }

        public static double CalculateProteoformMass(string pForm, Dictionary<char, double> aaIsotopeMassList)
        {
            double proteoformMass = 18.010565; // start with water
            char[] aminoAcids = pForm.ToCharArray();
            List<double> aaMasses = new List<double>();
            for (int i = 0; i < pForm.Length; i++)
            {
                if (aaIsotopeMassList.ContainsKey(aminoAcids[i])) aaMasses.Add(aaIsotopeMassList[aminoAcids[i]]);
            }
            return proteoformMass + aaMasses.Sum();
        }

        public string ptm_list_string()
        {
            if (ptm_list.Count == 0)
                return "unmodified";
            else
                return string.Join("; ", ptm_list.Select(ptm => ptm.modification.description));
        }
    }

    public class TheoreticalProteoformGroup : TheoreticalProteoform
    {

        public List<string> accessionList { get; set; } // this is the list of accession numbers for all proteoforms that share the same modified mass. the list gets alphabetical order

        public TheoreticalProteoformGroup(string accession, string description, string name, string fragment, int begin, int end, double unmodified_mass, int lysine_count, List<GoTerm> goTerms, PtmSet ptm_set, double modified_mass, bool is_target)
            : base(accession, description, name, fragment, begin, end, unmodified_mass, lysine_count, goTerms, ptm_set, modified_mass, is_target)
        { }
        public TheoreticalProteoformGroup(List<TheoreticalProteoform> theoreticals)
            : base(theoreticals[0].accession + "_T" + theoreticals.Count(), String.Join(";", theoreticals.Select(t => t.description)), String.Join(";", theoreticals.Select(t => t.description)), String.Join(";", theoreticals.Select(t => t.fragment)), theoreticals[0].begin, theoreticals[0].end, theoreticals[0].unmodified_mass, theoreticals[0].lysine_count, theoreticals[0].goTerms, theoreticals[0].ptm_set, theoreticals[0].modified_mass, theoreticals[0].is_target)
        {
            this.accessionList = theoreticals.Select(p => p.accession).ToList();
        }
    }

    public class weightedRatioIntensityVariance
    {
        public double ratio { get; set; } = 0;
        public double intensity { get; set; } = 0;
        public double variance { get; set; } = 0;
        public double pValue { get; set; } = 0;
    }

}
