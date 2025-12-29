#include "SafetyDetector.h"

vector<Rect> LadderDetector::runDetection(Mat& frame, vector<pair<Scalar, Scalar>>& colorRanges, const DetectorConfig& cfg) {
    vector<Rect> res;
    Mat blob = blobFromImage(frame, 0.007843, Size(300, 300), Scalar(127.5, 127.5, 127.5));
    net.setInput(blob);
    Mat prob = net.forward();
    Mat detM(prob.size[2], prob.size[3], CV_32F, prob.ptr<float>());

    Rect rawBox; float maxC = 0;
    for (int i = 0; i < detM.rows; i++) {
        if ((int)detM.at<float>(i, 1) == 15 && detM.at<float>(i, 2) > cfg.confidenceThreshold) {
            int x1 = (int)(detM.at<float>(i, 3) * frame.cols); int y1 = (int)(detM.at<float>(i, 4) * frame.rows);
            int x2 = (int)(detM.at<float>(i, 5) * frame.cols); int y2 = (int)(detM.at<float>(i, 6) * frame.rows);
            if (detM.at<float>(i, 2) > maxC) { maxC = detM.at<float>(i, 2); rawBox = Rect(x1, y1, x2 - x1, y2 - y1); }
        }
    }
    if (maxC > 0) { lastRect = rawBox; isFirstDetection = false; }
    else if (!isFirstDetection) rawBox = lastRect; else return res;

    Rect pBox = enlargeRectFixed(rawBox, 25, 60, frame.cols, frame.rows);
    Rect hROI = Rect(pBox.x, pBox.y, pBox.width, (int)(pBox.height * 0.28)) & Rect(0, 0, frame.cols, frame.rows);

    bool helmetOk = false; Mat debugViz = Mat::zeros(400, 400, CV_8UC3);
    if (hROI.width > 20) {
        Mat head = frame(hROI), cMask, eMask; vector<Vec3f> circles; float cC = 0, sC = 0, eC = 0;
        bool cR = detectHelmetByColor(head, cMask, cC);
        bool sR = detectHelmetByShape(head, circles, sC);
        detectHelmetByEdge(head, eMask, eC);
        helmetOk = (cR || sR);

        Mat temp = Mat::zeros(head.rows * 2, head.cols * 2, CV_8UC3);
        int w = head.cols, h = head.rows;
        Mat cV; cvtColor(cMask, cV, COLOR_GRAY2BGR); cV.copyTo(temp(Rect(0, 0, w, h)));
        Mat sV = head.clone(); for (auto& c : circles) circle(sV, Point(cvRound(c[0]), cvRound(c[1])), cvRound(c[2]), Scalar(0, 255, 0), 2);
        sV.copyTo(temp(Rect(w, 0, w, h)));
        Mat eV; cvtColor(eMask, eV, COLOR_GRAY2BGR); eV.copyTo(temp(Rect(0, h, w, h)));
        head.copyTo(temp(Rect(w, h, w, h)));
        resize(temp, debugViz, Size(400, 400));
    }

    Mat canvas = Mat::zeros(600, 1200, CV_8UC3);
    Mat mainV; resize(frame, mainV, Size(800, 600));
    Rect sBox = scaleRect(pBox, frame.cols, frame.rows);
    rectangle(mainV, sBox, helmetOk ? Scalar(0, 255, 0) : Scalar(0, 0, 255), 2);
    putText(mainV, helmetOk ? "SAFE" : "NO HELMET", Point(sBox.x, sBox.y - 10), 0, 0.8, helmetOk ? Scalar(0, 255, 0) : Scalar(0, 0, 255), 2);

    mainV.copyTo(canvas(Rect(0, 0, 800, 600)));
    debugViz.copyTo(canvas(Rect(800, 100, 400, 400)));
    putText(canvas, "LADDER MODE", Point(810, 30), 0, 0.6, Scalar(0, 255, 0), 1);
    imshow("Safety Integration Dashboard", canvas);
    return res;
}