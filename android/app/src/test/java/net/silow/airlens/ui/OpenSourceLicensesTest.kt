package net.silow.airlens.ui

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

class OpenSourceLicensesTest {
    /** Unit tests run in the module folder, so the resource is read from source. */
    private val license = File("src/main/res/raw/apache_license_2_0.txt").readText()

    @Test
    fun licenseTextIsTheWholeApacheLicense() {
        assertTrue(license.trimStart().startsWith("Apache License"))
        assertTrue(license.contains("Version 2.0, January 2004"))
        assertTrue(license.contains("4. Redistribution."))
        assertTrue(license.trimEnd().endsWith("END OF TERMS AND CONDITIONS"))
    }

    @Test
    fun everyProjectNamesItsCopyrightAndSource() {
        assertTrue(bundledOpenSource.size >= 8)
        bundledOpenSource.forEach {
            assertTrue(it.name, it.copyright.startsWith("Copyright "))
            assertTrue(it.name, it.url.startsWith("https://"))
        }
    }
}
