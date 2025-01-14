﻿using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.UI;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using System.Linq;
using TMPro;
using Tayx.Graphy;

public class PhoneCamera : MonoBehaviour
{

	private bool camAvailable;
	private WebCamTexture cameraTexture;
	private Texture defaultBackground;
    private bool detected;

    private float updateRateSeconds = 4.0F;
    private int frameCount = 0;
    private float dt = 0.0F;
    private float fps = 0.0F;

    Mat cameraMat;
    Texture2D outputTexture;
    Texture2D maskROITex2D;
    Color32[] colors;

    public RawImage background;
	public AspectRatioFitter fit;
	public bool frontFacing;
    public int cameraWidth;
    public int cameraHeight;
    public int cameraFPS;
    public TextMeshProUGUI log;
    public GameObject virtualPlane;
    public Transform cameraTransform;

    //Parametros
    public int qtdKeys;

    // Use this for initialization
    void Start()
	{
        //Estado inicial da detcção
        detected = false;

        // Make the game run as fast as possible
        Application.targetFrameRate = 300;
        QualitySettings.vSyncCount = 0;

        defaultBackground = background.texture;
		WebCamDevice[] devices = WebCamTexture.devices;

		if (devices.Length == 0)
			return;

        Debug.Log("Number of web cams connected: " + devices.Length);

       /* for (int i = 0; i < devices.Length; i++)
		{
			var curr = devices[i];

			if (curr.isFrontFacing == frontFacing)
			{
				cameraTexture = new WebCamTexture(curr.name, Screen.width, Screen.height);
				break;
			}
		}*/

        var curr = devices[0];
        cameraTexture = new WebCamTexture(curr.name, Screen.width, Screen.height);

        if (cameraTexture == null)
            return;

        //Configurações da câmera
        if (cameraWidth != 0) cameraTexture.requestedWidth = cameraWidth;
        if (cameraHeight != 0) cameraTexture.requestedHeight = cameraHeight;
        if (cameraFPS != 0) cameraTexture.requestedFPS = cameraFPS;

        Debug.Log("Device connected: " + cameraTexture.deviceName);
        log.text = "Device connected: " + cameraTexture.deviceName;
        Debug.Log("Camera Width: " + cameraTexture.requestedWidth.ToString());
        Debug.Log("Camera Height: " + cameraTexture.requestedHeight.ToString());
        Debug.Log("Camera FPS: " + cameraTexture.requestedFPS.ToString());

        cameraTexture.Play(); // Start the camera
        background.texture = cameraTexture; // Set the texture

        cameraMat = new Mat(cameraTexture.height, cameraTexture.width, CvType.CV_8UC4);
        outputTexture = new Texture2D(cameraTexture.width, cameraTexture.height, TextureFormat.ARGB32, false);
        background.texture = outputTexture; // Set the texture

        camAvailable = true; // Set the camAvailable for future purposes.
	}

    Mat TadaptiveMeanBin(Mat gray, int scale, int block_size, int sub_mean, Size morph_kernel, int morph_type, bool negative)
    {
        //Reduzir imagem
        int width = (int)(gray.width() * scale / 100);
        int height = (int)(gray.height() * scale / 100);
        Mat dim = new Mat(height, width, CvType.CV_8UC4);
        Imgproc.resize(gray, dim, dim.size(), 0, 0, Imgproc.INTER_LINEAR);
        //Aplicar Threshold adaptativo
        Mat th = new Mat(height, width, CvType.CV_8UC4);
        Imgproc.adaptiveThreshold(dim, th, 255, Imgproc.ADAPTIVE_THRESH_MEAN_C, Imgproc.THRESH_BINARY, block_size, sub_mean);
        //Aplicar filtro para reduzir ruidos
        Mat kernel = new Mat((int)morph_kernel.height, (int)morph_kernel.width, CvType.CV_8U, new Scalar(255));
        //Mat kernel = Imgproc.getStructuringElement(Imgproc.MORPH_ELLIPSE, morph_kernel); //Teste kernel ellipse
        Mat morph = new Mat(height, width, CvType.CV_8UC4);
        Imgproc.morphologyEx(th, morph, morph_type, kernel);
        //Voltar ao tamanho original
        width = gray.width();
        height = gray.height();
        Mat defaultDim = new Mat(height, width, CvType.CV_8UC4);
        Imgproc.resize(morph, defaultDim, defaultDim.size(), 0, 0, Imgproc.INTER_LINEAR);
        // Filtro negativo na imagem (Inverter cores)
        Mat result = new Mat(height, width, CvType.CV_8UC4);
        if (negative == true)
        {
            Core.bitwise_not(defaultDim, result);
        }
        //Se não, usa bitwise_and para copiar defaultDim para result
        else Core.bitwise_and(defaultDim, defaultDim, result);
        return result;
    }

    Mat auto_canny(Mat image, float sigma = 0.33f)
    {
        //compute the median of the single channel pixel intensities
        Scalar v = Core.mean(image);
        //apply automatic Canny edge detection using the computed median
        int lower = (int)(Mathf.Max(0, (1 - sigma) * (float)v.val[0]));

        int upper = (int)(Mathf.Min(255, (1 + sigma) * (float)v.val[0]));

        Mat edged = new Mat();
        Imgproc.Canny(image, edged, lower, upper);
        // return the edged image
        return edged;
    }

    Mat removeSombras(Mat img)
    {
        List<Mat> rgb_planes = new List<Mat>();
        Core.split(img, rgb_planes);
        List<Mat> result_norm_planes = new List<Mat>();
        foreach (Mat plane in rgb_planes)
        {
            Mat kernel = new Mat(7, 7, CvType.CV_8U, new Scalar(255));
            Mat dilated_img = new Mat();
            Imgproc.dilate(plane, dilated_img, kernel);
            Mat bg_img = new Mat();
            Imgproc.medianBlur(dilated_img, bg_img, 21);
            Mat white = new Mat(bg_img.height(), bg_img.width(), CvType.CV_8UC4, new Scalar(255));
            Mat diff_img = new Mat();
            Core.absdiff(plane, bg_img, diff_img);
            Core.subtract(white, diff_img, diff_img);
            Mat norm_img = new Mat();
            Core.normalize(diff_img, norm_img, 0, 255, Core.NORM_MINMAX, CvType.CV_8UC1);
            result_norm_planes.Add(norm_img);
        }
        Mat result = new Mat();
        Core.merge(result_norm_planes, result);
        return result;
    }

    // Update is called once per frame
    void Update()
	{
		if (!camAvailable)
			return;

		float ratio = (float)cameraTexture.width / (float)cameraTexture.height;
		fit.aspectRatio = ratio; // Set the aspect ratio

		float scaleY = cameraTexture.videoVerticallyMirrored ? -1f : 1f; // Find if the camera is mirrored or not
		background.rectTransform.localScale = new Vector3(1f, scaleY, 1f); // Swap the mirrored camera

		int orient = -cameraTexture.videoRotationAngle;
		background.rectTransform.localEulerAngles = new Vector3(0, 0, orient);


        ////Teclado
        //if (Input.GetKeyDown(KeyCode.KeypadPlus))
        //{
        //    areaExcludeValue += 0.01;
        //}
        //else if (Input.GetKeyDown(KeyCode.KeypadMinus))
        //{
        //    areaExcludeValue -= 0.01;
        //}

        //---------------------------------------------------
        //Ler frame da câmera e converter para MAT do OpenCV
        //---------------------------------------------------
        outputTexture.SetPixels32(cameraTexture.GetPixels32());
        outputTexture.Apply();
        Utils.texture2DToMat(outputTexture, cameraMat, false);

        //Enquanto não tiver detectado
        if (detected == false)
        {

            //Converter em escala de cinza
            Mat gray = new Mat(Screen.height, Screen.width, CvType.CV_8UC4);
            Imgproc.cvtColor(cameraMat, gray, Imgproc.COLOR_BGR2GRAY);

            //Calcular area do frame
            double frame_area = Screen.height * Screen.width;

            //Threshold binário
            Mat th = new Mat();
            Mat kernel = new Mat(5, 5, CvType.CV_8U, new Scalar(255));
            Mat blur = new Mat();
            Imgproc.GaussianBlur(gray, blur, new Size(9, 9), 3);
            Imgproc.threshold(blur, th, 80, 255, Imgproc.THRESH_BINARY_INV);
            Imgproc.erode(th, th, kernel, new Point(), 1);
            //Imgproc.dilate(brancas, brancas, kernel, new Point(), 1);


            //--------------------------------
            // DECLARAÇÕES
            //--------------------------------
            //Contador de contornos detectados
            int detectedCount = 0;
            //Pontos máximos e mínimos da área detectada
            // D __ C
            //  |  |
            //  |__|
            // A    B
            Point A = new Point(cameraMat.width(), cameraMat.height());
            Point B = new Point(0, cameraMat.height());
            Point C = new Point(0, 0);
            Point D = new Point(cameraMat.width(), 0);
            //Vertices usados para retângulo rotacionado
            Point[] vertices = new Point[4];
            //Lista de contornos usada para drawContour
            List<MatOfPoint> boxContours = new List<MatOfPoint>();


            //--------------------------------
            // ROI
            //--------------------------------
            //Definir tamanho do ROI: roi.height = Screen.Width/6; (Teeclas:84x14, então AspectRatio=84/14 = 6)
            double roiSize = cameraMat.width() / 6;
            //Retângulos do ROI
            OpenCVForUnity.CoreModule.Rect roiExcludeRectDown = new OpenCVForUnity.CoreModule.Rect(0, 0, cameraMat.width(), (int)((cameraMat.height() / 2) - roiSize / 2));
            OpenCVForUnity.CoreModule.Rect roiExcludeRectUp = new OpenCVForUnity.CoreModule.Rect(0, (int)((cameraMat.height() / 2) + roiSize / 2), cameraMat.width(), cameraMat.height());
            OpenCVForUnity.CoreModule.Rect roiRect = new OpenCVForUnity.CoreModule.Rect(0, (int)((cameraMat.height() / 2) - roiSize / 2), cameraMat.width(), (int)(roiSize));
            //Criar máscara toda preta
            Mat mask = new Mat(cameraMat.rows(), cameraMat.cols(), CvType.CV_8U, Scalar.all(0));
            //Desenhar roiRect em branco na máscara
            Imgproc.rectangle(mask, roiRect, Scalar.all(255), Imgproc.FILLED);
            //Aplicar filtro com máscara
            Mat imgROI = new Mat();
            th.copyTo(imgROI, mask);

            //Contornos
            List<MatOfPoint> contours = new List<MatOfPoint>();
            Imgproc.findContours(imgROI, contours, new Mat(), Imgproc.RETR_TREE, Imgproc.CHAIN_APPROX_TC89_L1);
            foreach (MatOfPoint c in contours)
            {
                //Contorno convertido em MatOfPoint2f
                MatOfPoint2f c2f = new MatOfPoint2f();
                c.convertTo(c2f, CvType.CV_32FC2);

                //----------------------
                // Métricas do contorno
                //----------------------
                //Área
                double cArea = Imgproc.contourArea(c);
                //Perímetro
                double cPerimeter = Imgproc.arcLength(c2f, true);
                //Aspect Ratio (H/W)
                double cAR = c.height() / c.width();

                //-------------------------
                // Aproximações do contorno
                //-------------------------
                //Retângulo máximo
                OpenCVForUnity.CoreModule.Rect bRect = Imgproc.boundingRect(c);
                //Retângulo rotacionado
                RotatedRect rRect = Imgproc.minAreaRect(c2f);
                //Círculo
                Point cCenter = new Point();
                float[] cRadius = new float[1];
                Imgproc.minEnclosingCircle(c2f, cCenter, cRadius);
                //Aproximação ApproxPolyDP
                MatOfPoint2f cPoly = new MatOfPoint2f();
                Imgproc.approxPolyDP(c2f, cPoly, 3, true);


                //Eliminar áreas muito pequenas (ruídos) nos contornos
                double perc_area = (cArea * 100 / frame_area);
                if (perc_area < 0.05)
                {
                    continue;
                }


                //---------------------------
                // Selecionar contornos úteis
                //---------------------------

                //Aspect Ratio do retângulo rotacionado (H/W)
                double rrAR = rRect.size.height / rRect.size.width;
                if (Mathf.Abs((float)rRect.angle) > 45) rrAR = rRect.size.width / rRect.size.height;

                //Selecionar contornos com Aspect Ratio do Retângulo Rotacionado entre 3 e 12
                if (rrAR > 3 && rrAR < 12)
                {
                    //Desenhar aproximação ApproxPolyDP
                    //MatOfPoint approxContour = new MatOfPoint();
                    //cPoly.convertTo(approxContour, CvType.CV_32S);
                    //List<MatOfPoint> boxContours = new List<MatOfPoint>();
                    //boxContours.Add(approxContour);
                    //Imgproc.drawContours(cameraMat, boxContours, 0, new Scalar(255, 0, 0), 2);

                    //Desenhar Retângulo rotacionado
                    rRect.points(vertices);
                    boxContours.Clear();
                    boxContours.Add(new MatOfPoint(vertices));
                    Imgproc.drawContours(cameraMat, boxContours, 0, new Scalar(255, 0, 0), 6, Imgproc.LINE_AA);

                    //Setar pontos A,B,C e D
                    // D __ C
                    //  |  |
                    //  |__|
                    // A    B
                    //Ordenar pontos primeiro por Y, depois por X usando System.Linq
                    List<Point> rrPoints = new List<Point>();
                    rrPoints = vertices.OrderBy(p => p.y).ThenBy(p => p.x).ToList<Point>();
                    //Ordem:
                    //rrPoints[0] = A
                    //rrPoints[1] = B
                    //rrPoints[2] = D
                    //rrPoints[3] = C

                    //Debug.Log("-ORDENACAO-");
                    //Debug.Log(rrPoints[0].ToString());
                    //Debug.Log(rrPoints[1].ToString());
                    //Debug.Log(rrPoints[2].ToString());
                    //Debug.Log(rrPoints[3].ToString());

                    if (rrPoints[0].x < A.x) A.x = rrPoints[0].x;
                    if (rrPoints[0].y < A.y) A.y = rrPoints[0].y;
                    if (rrPoints[1].x > B.x) B.x = rrPoints[1].x;
                    if (rrPoints[1].y < B.y) B.y = rrPoints[1].y;
                    if (rrPoints[3].x > C.x) C.x = rrPoints[3].x;
                    if (rrPoints[3].y > C.y) C.y = rrPoints[3].y;
                    if (rrPoints[2].x < D.x) D.x = rrPoints[2].x;
                    if (rrPoints[2].y > D.y) D.y = rrPoints[2].y;

                    //Incrementar contador de selecionados/detectados
                    detectedCount++;
                }


                //Desenhar circulo
                //Imgproc.circle(cameraMat, cCenter, (int)cRadius[0], new Scalar(0, 0, 255), 2);

                //Desenhar retângulo máximo
                //Imgproc.rectangle(cameraMat, bRect, new Scalar(0, 0, 255), 1, Imgproc.LINE_AA);

                //Desenhar Retângulo rotacionado
                //rRect.points(vertices);
                //boxContours.Clear();
                //boxContours.Add(new MatOfPoint(vertices));
                //Imgproc.drawContours(cameraMat, boxContours, 0, new Scalar(255, 0, 0), 1);

                //Imprimir AspectRatio
                //Imgproc.putText(cameraMat, " " + rrAR.ToString("0.##"), cCenter, Imgproc.FONT_HERSHEY_SIMPLEX, 0.5, new Scalar(255, 0, 0), 1, Imgproc.LINE_AA, true);

            }

            Debug.Log("Teclas detectadas: " + detectedCount.ToString());

            //Comparar quantidade de teclas detectadas com qtdKeys
            //A cada 12 teclas, 5 são pretas
            if ( Mathf.FloorToInt(qtdKeys * 5 / 12) == detectedCount)
            {
                //detected = true;
                Debug.Log("Quantidade Pretas: " + (Mathf.FloorToInt(qtdKeys * 5 / 12)).ToString());
            }

            //-------------------------------
            // Definir área das teclas pretas
            //-------------------------------
            //Desenhar área das teclas pretas
            Point[] blackKeysPoints = new Point[4];
            blackKeysPoints[0] = A;
            blackKeysPoints[1] = D;
            blackKeysPoints[2] = C;
            blackKeysPoints[3] = B;
            MatOfPoint blackKeysArea = new MatOfPoint(blackKeysPoints);
            boxContours.Clear();
            boxContours.Add(blackKeysArea);
            Imgproc.drawContours(cameraMat, boxContours, 0, new Scalar(0, 0, 255), 2);

            //Criar retangulo rotacionado
            MatOfPoint2f rRectAreaPoints = new MatOfPoint2f(blackKeysPoints);
            RotatedRect rRectArea = Imgproc.minAreaRect(rRectAreaPoints);

            //Reposicionar VirtualPlane
            //Vector3 relativePosition = cameraTransform.InverseTransformDirection(virtualPlane.transform.position - cameraTransform.position);
            virtualPlane.transform.localPosition = new Vector3((float)rRectArea.center.x, (float)rRectArea.center.y, virtualPlane.transform.localPosition.z);



            //-------------------------------
            // Desenhar ROI
            //-------------------------------
            //Imgproc.line(cameraMat, new Point(0, (cameraMat.height() / 2) - roiSize / 2), new Point(cameraMat.width(), (cameraMat.height() / 2) - roiSize / 2), new Scalar(255,255,255));
            //Imgproc.line(cameraMat, new Point(0, (cameraMat.height() / 2) + roiSize / 2), new Point(cameraMat.width(), (cameraMat.height() / 2) + roiSize / 2), new Scalar(255, 255, 255));
            //Criar Mat com os retângulos
            Mat roiExcluded = new Mat(cameraMat.rows(), cameraMat.cols(), CvType.CV_8UC4, Scalar.all(0));
            Imgproc.rectangle(roiExcluded, roiExcludeRectUp, Scalar.all(255), Imgproc.FILLED);
            Imgproc.rectangle(roiExcluded, roiExcludeRectDown, Scalar.all(255), Imgproc.FILLED);
            //Aplicar transparência
            Core.addWeighted(roiExcluded, 0.50, cameraMat, 1.0, 1.0, cameraMat);

        }

        //-------------------------------
        // FRAMERATE
        //-------------------------------
        frameCount++;
        dt += Time.unscaledDeltaTime;
        if (dt > 1.0 / updateRateSeconds)
        {
            fps = frameCount / dt;
            frameCount = 0;
            dt -= 1.0F / updateRateSeconds;
        }

        //Imgproc.putText(cameraMat, " " + (avgFrameRate).ToString("0.##"), new Point(150, 15), Imgproc.FONT_HERSHEY_SIMPLEX, 1, new Scalar(255, 0, 0), 1, Imgproc.LINE_AA, true);
        log.text = "Res.:"+cameraMat.width().ToString()+" x "+ cameraMat.height().ToString() + " FPS: "+(fps).ToString("0.##");

        //Imgproc.resize(imgROI, imgROI, cameraMat.size());
        //Imgproc.cvtColor(imgROI, cameraMat, Imgproc.COLOR_GRAY2RGB);
        //Imgproc.putText(cameraMat, "Tamanho do FRAME: " + cameraTexture.width + "x" + cameraTexture.height, new Point(5, cameraTexture.height - 5), Imgproc.FONT_HERSHEY_SIMPLEX, 2.0, new Scalar(255, 0, 0, 255));

        //-------------------------------------------------------
        //Converter MAT do OpenCV para textura mapeada na câmera
        //------------------------------------------------------
        Utils.matToTexture2D(cameraMat, outputTexture, false);

    }
}
